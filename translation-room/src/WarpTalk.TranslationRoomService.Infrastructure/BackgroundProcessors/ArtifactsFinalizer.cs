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

    public ArtifactsFinalizer(
        IUnitOfWork unitOfWork,
        IRedisStateRepository redisStateRepo,
        IAudioRouteEventProcessor eventProcessor,
        ILogger<ArtifactsFinalizer> logger,
        TranscriptService.TranscriptServiceClient transcriptClient,
        IOptions<ArtifactFinalizationSettings> options,
        ITranscriptCacheService transcriptCacheService)
    {
        _unitOfWork = unitOfWork;
        _redisStateRepo = redisStateRepo;
        _eventProcessor = eventProcessor;
        _logger = logger;
        _transcriptClient = transcriptClient;
        _settings = options.Value;
        _transcriptCacheService = transcriptCacheService;
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
                var room = await _unitOfWork.Repository<TranslationRoom>().GetByIdAsync(roomId, ct);
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
                var artifactRepo = _unitOfWork.Repository<TranslationRoomArtifact>();
                
                await artifactRepo.AddAsync(transcript, ct);
                await artifactRepo.AddAsync(summary, ct);

                await _unitOfWork.SaveChangesAsync(ct);

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
                    var segmentsReq = CreateGetTranscriptSegmentsRequest(transcript.Id);
                    var segmentsRes = await _transcriptClient.GetTranscriptSegmentsAsync(segmentsReq, cancellationToken: ct);

                    if (segmentsRes != null && segmentsRes.Segments.Any())
                    {
                        foreach (var seg in segmentsRes.Segments.OrderBy(s => s.SequenceOrder))
                        {
                            segmentsList.Add($"**[{seg.SpeakerName} ({seg.OriginalLanguage.ToUpper()})]**: {seg.OriginalText}");
                        }
                    }
                }
            }

            var fullTranscript = FormatTranscriptText(roomId, segmentsList);
            long sizeBytes = Encoding.UTF8.GetByteCount(fullTranscript);

            return BuildArtifactRequest(
                roomId, 
                ArtifactType.TRANSCRIPT_EXPORT, 
                null,
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false,
                content: fullTranscript)
                .ToEntity();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve real transcript from TranscriptService via gRPC. Falling back to local cache assembly.");
            
            // Graceful fallback to local cache assembly so that the system doesn't break if TranscriptService is not running
            var redisKey = CacheKeyHelper.GetTranscriptKey(roomId);
            string fullTranscript = await _transcriptCacheService.AssembleTranscriptAsync(roomId, redisKey);
            long sizeBytes = Encoding.UTF8.GetByteCount(fullTranscript);

            return BuildArtifactRequest(
                roomId, 
                ArtifactType.TRANSCRIPT_EXPORT, 
                null,
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false,
                content: fullTranscript)
                .ToEntity();
        }
    }

    private async Task<TranslationRoomArtifact> FinalizeSummaryAsync(Guid roomId, CancellationToken ct)
    {
        _logger.LogInformation("Retrieving AI summary from Redis cache for room {RoomId}", roomId);

        try
        {
            // Try to fetch AI-generated summary from Redis hash key "meeting:{roomId}:summary"
            string summaryKey = $"meeting:{roomId}:summary";

            var summaryContent = await _redisStateRepo.HashGetAsync(summaryKey, "content");
            var actionItems = await _redisStateRepo.HashGetAsync(summaryKey, "action_items");
            // WT-13: ai_assistant_worker also writes a structured JSON version of the same
            // summary/decisions/action-items when it can (see MeetingAssistant.generate_structured_summary).
            var structuredJson = await _redisStateRepo.HashGetAsync(summaryKey, "structured_json");

            var summaryText = FormatSummaryText(roomId, summaryContent, actionItems);
            long sizeBytes = Encoding.UTF8.GetByteCount(summaryText);
            string content = BuildStructuredSummaryContent(structuredJson, summaryContent, actionItems);

            // Clean up meeting summary key from Redis
            await _redisStateRepo.KeyDeleteAsync(summaryKey);

            return BuildArtifactRequest(
                roomId,
                ArtifactType.SUMMARY_EXPORT,
                null,
                "text/markdown",
                sizeBytes,
                false,
                false,
                false,
                content: content)
                .ToEntity();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve summary from Redis. Saving an explicit insufficient-data result.");

            string fallbackText = FormatSummaryText(roomId, null, null);
            long sizeBytes = Encoding.UTF8.GetByteCount(fallbackText);

            return BuildArtifactRequest(
                roomId,
                ArtifactType.SUMMARY_EXPORT,
                null,
                "text/markdown",
                sizeBytes,
                false,
                false,
                false,
                content: BuildStructuredSummaryContent(null, null, null))
                .ToEntity();
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

    private static GetTranscriptSegmentsRequest CreateGetTranscriptSegmentsRequest(string transcriptId)
    {
        return new GetTranscriptSegmentsRequest 
        { 
            TranscriptId = transcriptId, 
            Skip = 0, 
            Take = 1000 
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

    private static string FormatTranscriptText(Guid roomId, List<string> segments)
    {
        var header = $"# WarpTalk Transcription Room - Room: {roomId}\nGenerated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n---\n";
        
        return segments.Count > 0 
            ? header + string.Join("\n", segments)
            : header + "*No speech transcription recorded.*";
    }

    private static string FormatSummaryText(Guid roomId, string? summaryContent, string? actionItems)
    {
        var header = $"# WarpTalk AI Meeting Summary\nRoom ID: {roomId}\nGenerated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n---\n";
        var summarySection = summaryContent != null ? $"## Summary\n{summaryContent}\n" : string.Empty;
        var actionItemsSection = actionItems != null ? $"\n## AI Action Items\n{actionItems}\n" : string.Empty;

        return summarySection != string.Empty || actionItemsSection != string.Empty
            ? header + summarySection + actionItemsSection
            : header + "## Summary\n*No real-time summary could be generated by the AI Assistant worker.*\n\n## Key Takeaways\n- The meeting ended, but summary generation did not complete.\n- Review the transcript export or retry summary generation when the AI worker is available.";
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
    private static string BuildStructuredSummaryContent(string? structuredJson, string? summaryContent, string? actionItemsRaw)
    {
        if (!string.IsNullOrWhiteSpace(structuredJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(structuredJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("summary", out _))
                {
                    // Already in the shape the frontend expects — pass through verbatim.
                    return structuredJson;
                }
            }
            catch (JsonException)
            {
                // Fall through to the best-effort text-based reconstruction below.
            }
        }

        if (string.IsNullOrWhiteSpace(summaryContent) && string.IsNullOrWhiteSpace(actionItemsRaw))
        {
            return JsonSerializer.Serialize(new
            {
                summary = "The AI assistant could not generate a summary for this meeting (no transcript content was available or generation did not complete in time).",
                decisions = Array.Empty<string>(),
                actionItems = Array.Empty<object>(),
                insufficientData = true
            });
        }

        return JsonSerializer.Serialize(new
        {
            summary = summaryContent ?? string.Empty,
            decisions = Array.Empty<string>(),
            actionItems = ParseActionItemsMarkdown(actionItemsRaw),
            insufficientData = false
        });
    }

    /// <summary>
    /// Best-effort parse of MeetingAssistant.extract_action_items's plain-text output
    /// (format: "[ ] Action item - @assignee") into {owner, task} pairs.
    /// </summary>
    private static List<object> ParseActionItemsMarkdown(string? actionItemsRaw)
    {
        var result = new List<object>();
        if (string.IsNullOrWhiteSpace(actionItemsRaw)) return result;

        var lines = actionItemsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart('-', '*', ' ');
            if (line.StartsWith("[ ]") || line.StartsWith("[x]", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring(3).Trim();
            }
            if (string.IsNullOrWhiteSpace(line)) continue;

            var atIndex = line.LastIndexOf(" - @", StringComparison.Ordinal);
            if (atIndex >= 0)
            {
                var task = line.Substring(0, atIndex).Trim();
                var owner = line.Substring(atIndex + 4).Trim();
                result.Add(new { owner, task });
            }
            else
            {
                result.Add(new { owner = "", task = line });
            }
        }

        return result;
    }

    #endregion
}
