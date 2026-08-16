using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.BackgroundProcessors;

public class ArtifactsFinalizer : IArtifactsFinalizer
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRedisStateRepository _redisStateRepo;
    private readonly IAudioRouteEventProcessor _eventProcessor;
    private readonly ILogger<ArtifactsFinalizer> _logger;
    private readonly TranscriptService.TranscriptServiceClient _transcriptClient;
    private readonly ArtifactFinalizationSettings _settings;
    private readonly ITranscriptCacheService _transcriptCacheService;
    private readonly IKnowledgeFactRequestPublisher _knowledgeFactPublisher;

    public ArtifactsFinalizer(
        IUnitOfWork unitOfWork,
        IRedisStateRepository redisStateRepo,
        IAudioRouteEventProcessor eventProcessor,
        ILogger<ArtifactsFinalizer> logger,
        TranscriptService.TranscriptServiceClient transcriptClient,
        IOptions<ArtifactFinalizationSettings> options,
        ITranscriptCacheService transcriptCacheService,
        IKnowledgeFactRequestPublisher knowledgeFactPublisher)
    {
        _unitOfWork = unitOfWork;
        _redisStateRepo = redisStateRepo;
        _eventProcessor = eventProcessor;
        _logger = logger;
        _transcriptClient = transcriptClient;
        _settings = options.Value;
        _transcriptCacheService = transcriptCacheService;
        _knowledgeFactPublisher = knowledgeFactPublisher;
    }

    public async Task ProcessRoomFinalizationAsync(Guid roomId, CancellationToken ct = default)
    {
        _logger.LogInformation("Processing graceful flush and finalization for room {RoomId}", roomId);

        try
        {
            // WT-13 (best-effort, non-blocking): publish the room's configured target
            // language(s) for ai_assistant_worker to read when it builds the structured
            // summary, so a multi-target-language room gets a bilingual summary instead of
            // one arbitrarily-chosen language. There's no strict ordering guarantee against
            // the AI worker's own summary generation (triggered independently from
            // MeetingService.EndMeetingAsync) — this is a best-effort hint, not a contract.
            try
            {
                var room = await _unitOfWork.TranslationRoomRepository.GetByIdAsync(roomId, ct);
                if (room != null)
                {
                    var targetLanguages = LanguageHelper.ParseTargetLanguages(room.TargetLanguages);
                    await _redisStateRepo.StringSetAsync(
                        $"meeting:{roomId}:target_languages",
                        JsonSerializer.Serialize(targetLanguages),
                        TimeSpan.FromMinutes(10));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish target languages for room {RoomId}", roomId);
            }

            // 1. Graceful Flush: Wait for final chunk processed or 30s timeout via repository pub/sub
            string channelName = $"translationRoom:{roomId}:final_processed";
            bool completedGracefully = await _redisStateRepo.WaitForSignalAsync(channelName, TimeSpan.FromSeconds(30), ct);

            if (!completedGracefully)
            {
                _logger.LogWarning("Graceful flush timed out (30s) for room {RoomId}. Executing fallback emergency flush.", roomId);
            }
            else
            {
                _logger.LogInformation("Received event-driven final_processed completion signal for room {RoomId}", roomId);
            }

            // 2. Transition route state to SAVING_OUTPUTS.
            _logger.LogInformation("Transitioning room {RoomId} state to SAVING_OUTPUTS", roomId);
            var transitionResult = await _eventProcessor.ProcessEventAsync(
                roomId,
                null,
                AudioRoutingEventType.flush_runtime.ToString(),
                "{}",
                ct);

            if (transitionResult.IsSuccess)
            {
                // 3. Finalize Transcripts, Summaries, and Recording Artifacts in Parallel
                _logger.LogInformation("Executing finalization tasks for room {RoomId}...", roomId);
                await FinalizeRoomArtifactsAsync(roomId, ct);
            }
            else
            {
                _logger.LogError("Failed to transition room {RoomId} to SAVING_OUTPUTS. Error: {Error}", roomId, transitionResult.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in finalization worker for room {RoomId}", roomId);
        }
    }

    private async Task FinalizeRoomArtifactsAsync(Guid roomId, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting artifacts finalization for Translation Room {RoomId}", roomId);

        int maxRetries = _settings.MaxLocalRetries;
        bool success = false;

        for (int attempt = 1; attempt <= maxRetries && !success; attempt++)
        {
            try
            {
                // Meeting owns recording artifacts from its signed
                // recording_completed event. This finalizer owns transcript
                // and summary outputs only.
                var transcriptTask = FinalizeTranscriptAsync(roomId, ct);
                var summaryTask = FinalizeSummaryAsync(roomId, ct);

                await Task.WhenAll(transcriptTask, summaryTask);

                // Await to gather results
                var transcript = await transcriptTask;
                var summary = await summaryTask;

                // Save all generated artifacts into the DB
                var artifactRepo = _unitOfWork.TranslationRoomArtifactRepository;

                await artifactRepo.AddAsync(transcript, ct);
                await artifactRepo.AddAsync(summary, ct);

                await _unitOfWork.SaveChangesAsync(ct);

                // Only now, with the summary durably stored, is it worth indexing. Publishing
                // before the save would index a summary a later rollback erased.
                await PublishSummaryToKnowledgeAsync(roomId, summary.Content, ct);

                _logger.LogInformation("Artifacts successfully saved to database. Triggering event transcript_recording_summary_linked");

                // Trigger the transition to COMPLETED state
                var eventResult = await _eventProcessor.ProcessEventAsync(
                    roomId,
                    null,
                    AudioRoutingEventType.outputs_linked.ToString(),
                    "{}",
                    ct);

                if (!eventResult.IsSuccess)
                {
                    _logger.LogError("Failed to transition route status to COMPLETED for Room {RoomId}. Error: {Error}", roomId, eventResult.Error);
                    throw new Exception("State transition failed after saving artifacts.");
                }

                // Clean up temporary keys using repository
                await _redisStateRepo.KeyDeleteAsync(CacheKeyHelper.GetTranscriptKey(roomId));
                await _redisStateRepo.KeyDeleteAsync(CacheKeyHelper.GetTelemetryStateKey(roomId));

                _logger.LogInformation("Successfully finalized artifacts and completed Translation Room {RoomId}", roomId);
                success = true;
            }
            catch (Exception ex)
            {
                // Clean Architecture: Since we are in the Infrastructure layer, we can check EF Core and Npgsql types directly!
                bool isPermanentDbError = ex is Microsoft.EntityFrameworkCore.DbUpdateException ||
                                           (ex.InnerException != null && ex.InnerException is Npgsql.PostgresException);

                if (isPermanentDbError)
                {
                    _logger.LogError(ex, "Permanent Data Constraint error during finalization for Room {RoomId}", roomId);

                    // Immediately emit finalization_abandoned to end lifecycle
                    await _eventProcessor.ProcessEventAsync(
                        roomId, null, AudioRoutingEventType.finalization_abandoned.ToString(), "{}", ct);

                    throw; // Re-throw so the worker knows it failed permanently
                }

                _logger.LogWarning(ex, "Failure during artifacts finalization for Room {RoomId}. Attempt {Attempt} of {MaxRetries}", roomId, attempt, maxRetries);

                if (attempt == maxRetries)
                {
                    _logger.LogError("Exhausted all {MaxRetries} retries for Room {RoomId}", maxRetries, roomId);

                    // Emit finalization_failed to put into FAILED queue for Sweeper
                    await _eventProcessor.ProcessEventAsync(
                        roomId, null, AudioRoutingEventType.finalization_failed.ToString(), "{}", ct);

                    throw;
                }

                // Exponential backoff with jitter (e.g. 2s, 4s, 8s + random ms)
                int baseDelayMs = (int)Math.Pow(2, attempt) * 1000;
                int jitterMs = Random.Shared.Next(0, 1000);
                await Task.Delay(baseDelayMs + jitterMs, ct);
            }
        }
    }

    private async Task<TranslationRoomArtifact> FinalizeTranscriptAsync(Guid roomId, CancellationToken ct)
    {
        _logger.LogInformation("Retrieving real meeting transcript via gRPC for room {RoomId}", roomId);

        try
        {
            // 1. Get transcripts for this room
            var request = CreateGetTranscriptsRequest(roomId);
            var response = await _transcriptClient.GetTranscriptsByTranslationRoomIdAsync(request, cancellationToken: ct);

            var segmentsList = new List<string>();

            if (response != null && response.Transcripts.Any())
            {
                // Process segments for each transcript
                foreach (var transcript in response.Transcripts)
                {
                    segmentsList.AddRange(await ReadAllSegmentsAsync(transcript.Id, roomId, ct));
                }
            }

            // Reached only when the RPC answered. An empty list here is a real answer — the
            // meeting genuinely produced no speech — and is worth saying plainly.
            var fullTranscript = FormatTranscriptText(roomId, segmentsList);

            return BuildTranscriptArtifact(roomId, fullTranscript);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not read the transcript for room {RoomId} from TranscriptService. Trying the local cache.",
                roomId);

            // WT-431. This fallback used to end the story: it assembled from
            // translationRoom:{roomId}:transcript, and when that key was missing it emitted
            // "*No speech transcription recorded.*" — the SAME sentence a genuinely silent
            // meeting produces. Nothing has ever written that key (it appears twice in this
            // codebase, both times being deleted), so the fallback could only ever produce that
            // sentence, and a refused connection was published to the user as a confident
            // statement that nobody had spoken. 135 of 135 transcript exports in production said
            // it, including meetings with 405 stored segments.
            //
            // The cache read stays — it costs one LRANGE and would be the right answer if
            // anything ever populated it — but it is no longer allowed to speak for a failure.
            var cachedSegments = await _transcriptCacheService.ReadCachedSegmentsAsync(
                CacheKeyHelper.GetTranscriptKey(roomId));

            if (cachedSegments.Count > 0)
            {
                _logger.LogInformation(
                    "Recovered {Count} transcript lines for room {RoomId} from the local cache.",
                    cachedSegments.Count,
                    roomId);

                return BuildTranscriptArtifact(roomId, FormatTranscriptText(roomId, [.. cachedSegments]));
            }

            _logger.LogError(
                "No transcript could be produced for room {RoomId}: TranscriptService was unreachable and nothing was cached. "
                + "Writing an explicit unavailable artifact — the stored segments, if any, are still in TranscriptService and are not lost.",
                roomId);

            return BuildTranscriptArtifact(roomId, FormatUnavailableTranscriptText(roomId, ex));
        }
    }

    /// <summary>
    /// Every segment of a transcript, in order — paging until the service says there are no more.
    ///
    /// This used to be a single call with Take = 1000 and no check against the reported total, so
    /// a meeting longer than 1000 segments had its tail dropped with nothing said. That is a small
    /// number for this product: production already holds a 405-segment meeting, and segments run
    /// at roughly one per utterance. A transcript that silently stops two thirds of the way
    /// through is worse than one that fails, because it looks complete.
    /// </summary>
    private async Task<List<string>> ReadAllSegmentsAsync(string transcriptId, Guid roomId, CancellationToken ct)
    {
        const int pageSize = 1000;

        var lines = new List<string>();
        var skip = 0;

        while (true)
        {
            var segmentsRes = await _transcriptClient.GetTranscriptSegmentsAsync(
                CreateGetTranscriptSegmentsRequest(transcriptId, skip, pageSize),
                cancellationToken: ct);

            if (segmentsRes == null || segmentsRes.Segments.Count == 0) break;

            foreach (var seg in segmentsRes.Segments.OrderBy(s => s.SequenceOrder))
            {
                lines.Add($"**[{seg.SpeakerName} ({seg.OriginalLanguage.ToUpper()})]**: {seg.OriginalText}");
            }

            skip += segmentsRes.Segments.Count;

            // TotalCount is authoritative; the page-size comparison is the belt-and-braces exit so
            // a service that reports a wrong total cannot spin this loop forever.
            if (skip >= segmentsRes.TotalCount || segmentsRes.Segments.Count < pageSize) break;
        }

        if (lines.Count > pageSize)
        {
            _logger.LogInformation(
                "Read {Count} transcript segments across {Pages} pages for room {RoomId}.",
                lines.Count,
                (lines.Count + pageSize - 1) / pageSize,
                roomId);
        }

        return lines;
    }

    private static TranslationRoomArtifact BuildTranscriptArtifact(Guid roomId, string content) =>
        BuildArtifactRequest(
            roomId,
            ArtifactType.TRANSCRIPT_EXPORT,
            null,
            ArtifactFileFormats.Markdown,
            Encoding.UTF8.GetByteCount(content),
            false,
            false,
            false,
            content: content)
            .ToEntity();

    /// <summary>
    /// WT-369 — HOW LONG THE SUMMARY IS GIVEN TO SHOW UP.
    ///
    /// The transcript half of this finalization already waits up to 30s for its `final_processed`
    /// signal. The summary was given nothing at all: one Redis read, immediately. But the summary
    /// is produced by ai_assistant_worker, triggered independently from
    /// MeetingService.EndMeetingAsync — the comment at the top of ProcessRoomFinalizationAsync
    /// says in as many words that there is "no strict ordering guarantee" — and it is an LLM call
    /// over a whole meeting transcript, which is not instant.
    ///
    /// Longer than the transcript's window because it is waiting on generation, not on a flush.
    /// It exits the moment content appears, so a summary that is already there costs one read.
    /// </summary>
    private static readonly TimeSpan SummaryWaitTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SummaryPollInterval = TimeSpan.FromSeconds(2);

    private async Task<TranslationRoomArtifact> FinalizeSummaryAsync(Guid roomId, CancellationToken ct)
    {
        _logger.LogInformation("Retrieving AI summary from Redis cache for room {RoomId}", roomId);

        try
        {
            // Try to fetch AI-generated summary from Redis hash key "meeting:{roomId}:summary"
            string summaryKey = $"meeting:{roomId}:summary";

            // WT-13: ai_assistant_worker also writes a structured JSON version of the same
            // summary/decisions/action-items when it can (see MeetingAssistant.generate_structured_summary).
            var (summaryContent, actionItems, structuredJson) =
                await WaitForSummaryAsync(summaryKey, roomId, ct);

            string content = SummaryContentBuilder.Build(structuredJson, summaryContent, actionItems);

            // DELETE ONLY WHAT WE ACTUALLY READ.
            //
            // This used to delete unconditionally, which is what made the race permanent rather
            // than merely unlucky: the finalizer read an empty key, wrote an "insufficient data"
            // artifact, and then removed the key — so when the AI worker finished a few seconds
            // later it wrote a real summary into a key nobody would ever read again. Leaving the
            // key alone on the empty path means a late summary is still there to be recovered.
            bool foundSomething =
                !string.IsNullOrWhiteSpace(summaryContent)
                || !string.IsNullOrWhiteSpace(actionItems)
                || !string.IsNullOrWhiteSpace(structuredJson);

            if (foundSomething)
            {
                await _redisStateRepo.KeyDeleteAsync(summaryKey);
            }
            else
            {
                _logger.LogError(
                    "No AI summary appeared for room {RoomId} within {Seconds}s. Saving an insufficient-data summary artifact and KEEPING {SummaryKey} so a late result is not lost.",
                    roomId,
                    SummaryWaitTimeout.TotalSeconds,
                    summaryKey);
            }

            return BuildSummaryArtifact(roomId, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve summary from Redis. Saving an explicit insufficient-data result.");

            return BuildSummaryArtifact(roomId, SummaryContentBuilder.Build(null, null, null));
        }
    }

    /// <summary>
    /// WT-432. Two things were wrong with how this row was written, on all 135 of them in
    /// production.
    ///
    /// SizeBytes measured the wrong string. The old code built a markdown document with
    /// FormatSummaryText, counted ITS bytes, then stored <c>SummaryContentBuilder.Build(...)</c>
    /// instead and threw the markdown away — so the size on the row described a document that was
    /// never saved, and the markdown builder existed solely to be measured.
    ///
    /// FileFormat said markdown for a payload that is JSON. The frontend reads this content with
    /// parseMeetingSummaryContent, so JSON is the correct and intended storage shape — the label
    /// was the part that was wrong. It also has to be a token the download switch in
    /// TranslationRoomArtifactService recognises ("json"), not a MIME string; "text/markdown" fell
    /// through that switch's default and served both artifact kinds as .txt/text-plain.
    /// </summary>
    private static TranslationRoomArtifact BuildSummaryArtifact(Guid roomId, string content) =>
        BuildArtifactRequest(
            roomId,
            ArtifactType.SUMMARY_EXPORT,
            null,
            ArtifactFileFormats.Json,
            Encoding.UTF8.GetByteCount(content),
            false,
            false,
            false,
            content: content)
            .ToEntity();

    /// <summary>
    /// Polls the summary hash until the AI worker has written something, or the window closes.
    ///
    /// Returns whatever is there at the end — an empty result is a legitimate answer that the
    /// caller turns into an explicit insufficient-data artifact, because a UI stuck on
    /// "generating" forever is worse than one that says the summary did not arrive (WT-13).
    /// </summary>
    private async Task<(string? Content, string? ActionItems, string? StructuredJson)> WaitForSummaryAsync(
        string summaryKey,
        Guid roomId,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + SummaryWaitTimeout;
        var logged = false;

        while (true)
        {
            var content = await _redisStateRepo.HashGetAsync(summaryKey, "content");
            var actionItems = await _redisStateRepo.HashGetAsync(summaryKey, "action_items");
            var structuredJson = await _redisStateRepo.HashGetAsync(summaryKey, "structured_json");

            if (!string.IsNullOrWhiteSpace(content)
                || !string.IsNullOrWhiteSpace(actionItems)
                || !string.IsNullOrWhiteSpace(structuredJson))
            {
                return (content, actionItems, structuredJson);
            }

            if (DateTime.UtcNow >= deadline || ct.IsCancellationRequested)
            {
                return (content, actionItems, structuredJson);
            }

            if (!logged)
            {
                // Once, not every poll: this is the normal case for a meeting that has just
                // ended, and it should read as "waiting", not as a fault.
                _logger.LogInformation(
                    "Summary for room {RoomId} is not in Redis yet — waiting up to {Seconds}s for ai_assistant_worker.",
                    roomId,
                    SummaryWaitTimeout.TotalSeconds);
                logged = true;
            }

            await Task.Delay(SummaryPollInterval, ct);
        }
    }

    /// <summary>
    /// Hands the finished summary to the workspace knowledge index.
    ///
    /// Until this existed, a meeting's summary was written as an artifact and indexed by
    /// nobody, while every transcript segment was indexed individually — so the workspace
    /// Knowledge page could show hundreds of one-sentence rows from a meeting and not the one
    /// paragraph that actually described it.
    ///
    /// Wrapped in its own try/catch, and awaited rather than fired and forgotten: an
    /// unobserved task here would surface as an unhandled exception long after this scope's
    /// DbContext was disposed.
    /// </summary>
    private async Task PublishSummaryToKnowledgeAsync(Guid roomId, string? content, CancellationToken ct)
    {
        try
        {
            var text = MeetingSummaryKnowledgeText.Build(content);
            if (string.IsNullOrWhiteSpace(text)) return;

            var room = await _unitOfWork.TranslationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null || room.WorkspaceId == Guid.Empty) return;

            await _knowledgeFactPublisher.PublishAsync(
                room.WorkspaceId,
                "meeting_summary",
                roomId,
                room.Title,
                text,
                indexSourceText: true,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not index the summary for room {RoomId}", roomId);
        }
    }

    #region Static Factories & String Helpers (Ensures Zero "new" in Workflow Methods)

    private static GetTranscriptsByTranslationRoomRequest CreateGetTranscriptsRequest(Guid roomId)
    {
        return new GetTranscriptsByTranslationRoomRequest
        {
            TranslationRoomId = roomId.ToString()
        };
    }

    private static GetTranscriptSegmentsRequest CreateGetTranscriptSegmentsRequest(
        string transcriptId,
        int skip,
        int take)
    {
        return new GetTranscriptSegmentsRequest
        {
            TranscriptId = transcriptId,
            Skip = skip,
            Take = take
        };
    }

    private static CreateArtifactRequest BuildArtifactRequest(
        Guid roomId,
        ArtifactType artifactType,
        string? fileUrl,
        string fileFormat,
        long sizeBytes,
        bool containsRawAudio,
        bool containsRawVideo,
        bool consentRequired,
        DateTime? retentionUntil = null,
        string? content = null)
    {
        return new CreateArtifactRequest(
            roomId,
            artifactType.ToString(),
            fileUrl,
            fileFormat,
            sizeBytes,
            containsRawAudio,
            containsRawVideo,
            consentRequired,
            retentionUntil,
            content
        );
    }

    private static string TranscriptHeader(Guid roomId) =>
        $"# WarpTalk Transcription Room - Room: {roomId}\nGenerated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n---\n";

    private static string FormatTranscriptText(Guid roomId, List<string> segments)
    {
        return segments.Count > 0
            ? TranscriptHeader(roomId) + string.Join("\n", segments)
            : TranscriptHeader(roomId) + "*No speech transcription recorded.*";
    }

    /// <summary>
    /// The transcript could not be READ — which is not the same as there being nothing to read,
    /// and must not be written as if it were. Says which of the two happened, and that the
    /// segments are still held by TranscriptService, so whoever reads this knows there is
    /// something to recover rather than a meeting to re-run.
    /// </summary>
    private static string FormatUnavailableTranscriptText(Guid roomId, Exception cause)
    {
        return TranscriptHeader(roomId)
            + "*The transcript could not be retrieved when this meeting was finalized.*\n\n"
            + "This is **not** a statement that nobody spoke — the transcript service could not be "
            + "reached, so the recorded speech could not be read. Any segments captured during the "
            + "meeting are still stored by the transcript service and are not lost.\n\n"
            + $"Reason: {cause.GetType().Name}: {cause.Message}\n";
    }

    /// <summary>
    /// Builds the structured JSON stored on TranslationRoomArtifact.Content for a
    /// SUMMARY_EXPORT artifact: { summary, decisions[], actionItems[{owner, task}], insufficientData }.
    /// Prefers the AI worker's own structured JSON (ai_assistant_worker/assistant.py
    /// generate_structured_summary) when present and valid; otherwise falls back to
    /// best-effort parsing of the plain-text summary/action-items fields; otherwise reports
    /// insufficientData so the UI can show "not enough data" instead of hanging on a
    /// perpetual "generating" state (WT-13 requirement).
    /// </summary>
    #endregion
}
