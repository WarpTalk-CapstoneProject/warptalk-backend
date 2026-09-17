using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Application.Services;

/// <inheritdoc cref="ITranscriptTranslationBackfillService"/>
public class TranscriptTranslationBackfillService : ITranscriptTranslationBackfillService
{
    /// <summary>
    /// Where the work goes. Deliberately NOT <c>stt:results</c>, which is what translation_worker
    /// reads: that path gates on a live room ("translation_skipped_not_started"), takes its target
    /// languages from the presence hash the gateway deletes when the last participant leaves, and
    /// feeds the TTS worker. A finished meeting satisfies none of that and wants none of the audio.
    /// </summary>
    public const string RequestStream = "translate:backfill_requests";

    /// <summary>
    /// One stream entry carries this many segments. The worker translates them in a single
    /// OpenAI call, so the batch size is a latency/robustness trade: too large and one refusal
    /// costs the whole group, too small and 178 lines become 178 round trips.
    /// </summary>
    public const int SegmentsPerRequest = 20;

    /// <summary>
    /// A ceiling on what one request may queue. The longest real meeting on record is ~750
    /// segments; this is the "somebody points it at every language of every transcript" guard,
    /// not a working limit.
    /// </summary>
    public const int MaxSegmentsPerRun = 3000;

    /// <summary>
    /// How long the run marker outlives the request. Long enough that a slow backfill still reads
    /// as running, short enough that a worker that died mid-run stops claiming to be alive — the
    /// marker is a hint for the UI, never a lock the correctness depends on.
    /// </summary>
    public static readonly TimeSpan RunMarkerTtl = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How many segment translations one transcript may queue inside <see cref="BudgetWindow"/>,
    /// across every language and both paths (gap fill and post-correction retranslation).
    ///
    /// Every queued line is a billed LLM call (billing_worker settles translate:backfill_results
    /// on the same TRANSLATION rate card as a live one), and nothing else bounds the total: the run
    /// marker only stops a duplicate of a run that is still alive, <see cref="MaxSegmentsPerRun"/>
    /// only bounds one language, and corrections redo every language of a line each time. Twice a
    /// full run: the longest real meeting (~750 lines) can still be read in eight languages in a
    /// day, while a script looping over languages or corrections stops at a known ceiling.
    /// </summary>
    public const int MaxQueuedSegmentsPerTranscriptPerWindow = 2 * MaxSegmentsPerRun;

    /// <summary>The fixed window the per-transcript budget counts over, from its first use.</summary>
    public static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(24);

    public const string StatusIdle = "idle";
    public const string StatusRunning = "running";
    public const string StatusComplete = "complete";
    public const string StatusFailed = "failed";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranscriptReadAccess _readAccess;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TranscriptTranslationBackfillService> _logger;

    public TranscriptTranslationBackfillService(
        IUnitOfWork unitOfWork,
        ITranscriptReadAccess readAccess,
        IConnectionMultiplexer redis,
        ILogger<TranscriptTranslationBackfillService> logger)
    {
        _unitOfWork = unitOfWork;
        _readAccess = readAccess;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>The Redis key a run marks itself alive under. Public so tests can assert on it.</summary>
    public static string RunMarkerKey(Guid transcriptId, string targetLanguage) =>
        $"transcript:backfill:{transcriptId}:{NormalizeLanguage(targetLanguage)}";

    /// <summary>The per-transcript budget counter. Public so tests can assert on it.</summary>
    public static string BudgetKey(Guid transcriptId) => $"transcript:backfill-budget:{transcriptId}";

    /// <summary>
    /// Segments carry bare ISO-639-1 from STT ("vi"), but a room can hand a locale tag ("vi-VN")
    /// to anything that asks it for its language. Comparing the two raw forms reports a Vietnamese
    /// line as missing Vietnamese, which would queue a translation of a sentence into its own
    /// language.
    /// </summary>
    public static string NormalizeLanguage(string? language)
    {
        var trimmed = (language ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var separator = trimmed.IndexOfAny(['-', '_']);
        var bare = separator > 0 ? trimmed[..separator] : trimmed;
        return bare.ToLowerInvariant();
    }

    /// <summary>
    /// Control markers (<c>__MEETING_END__</c> and friends) are pipeline signalling that was
    /// written into the transcript as if it were speech. They are already hidden from the reader;
    /// counting them here would make a fully covered transcript report a permanent shortfall, and
    /// queueing them would pay to translate the word "__MEETING_END__" into Japanese — which the
    /// live pipeline has actually done. Same for the "system" pseudo-language.
    /// </summary>
    public static bool IsTranslatableSegment(TranscriptSegment segment)
    {
        var text = segment.OriginalText?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^__[A-Z0-9_]+__"))
        {
            return false;
        }

        return !string.Equals(NormalizeLanguage(segment.OriginalLanguage), "system", StringComparison.Ordinal);
    }

    public async Task<Result<TranscriptLanguageCoverageDto>> GetCoverageAsync(
        Guid transcriptId,
        Guid userId,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var context = await LoadAsync(transcriptId, userId, targetLanguage, cancellationToken);
            if (!context.IsSuccess)
            {
                return Result.Failure<TranscriptLanguageCoverageDto>(context.Error!, context.ErrorCode);
            }

            return Result.Success(await DescribeAsync(context.Value!, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading language coverage for transcript {TranscriptId}", transcriptId);
            return Result.Failure<TranscriptLanguageCoverageDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<TranscriptLanguageCoverageDto>> RequestBackfillAsync(
        Guid transcriptId,
        Guid userId,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var context = await LoadAsync(transcriptId, userId, targetLanguage, cancellationToken);
            if (!context.IsSuccess)
            {
                return Result.Failure<TranscriptLanguageCoverageDto>(context.Error!, context.ErrorCode);
            }

            var work = context.Value!;
            if (work.Missing.Count == 0)
            {
                return Result.Success(await DescribeAsync(work, cancellationToken));
            }

            if (work.Missing.Count > MaxSegmentsPerRun)
            {
                return Result.Failure<TranscriptLanguageCoverageDto>(
                    $"This transcript needs {work.Missing.Count} lines translated, which is over the {MaxSegmentsPerRun} allowed in one request.",
                    "TOO_LARGE");
            }

            var db = _redis.GetDatabase();
            var markerKey = RunMarkerKey(transcriptId, work.Language);

            // NX: a second reader picking the same language while the first run is still going
            // must watch it, not queue every segment again. Losing this race is the normal
            // outcome, not an error — both callers want the same rows to exist.
            var claimed = await db.StringSetAsync(markerKey, StatusRunning, RunMarkerTtl, When.NotExists);
            if (!claimed)
            {
                // Unless the marker is a corpse. A run that failed leaves one behind for the rest
                // of its TTL, and refusing to start on that basis would tell the reader to try
                // again beside a button that cannot: twenty minutes of a retry that silently does
                // nothing. Two callers racing here both proceed, which costs a duplicate queue at
                // worst; a stuck marker costs the feature.
                var existing = await db.StringGetAsync(markerKey);
                if (existing != StatusFailed)
                {
                    return Result.Success(await DescribeAsync(work, cancellationToken));
                }

                await db.StringSetAsync(markerKey, StatusRunning, RunMarkerTtl);
            }

            // Reserved only by a run that is about to queue: an in-flight duplicate has already
            // returned above without spending anything.
            if (!await TryReserveBudgetAsync(db, transcriptId, work.Missing.Count))
            {
                // Release the marker just claimed: nothing is running, and leaving it would show a
                // progress bar for twenty minutes over a request that was refused.
                await db.KeyDeleteAsync(markerKey);
                _logger.LogWarning(
                    "Refused to backfill transcript {TranscriptId} into {Language}: {Segments} more lines would exceed its budget of {Budget} per {Window}",
                    transcriptId,
                    work.Language,
                    work.Missing.Count,
                    MaxQueuedSegmentsPerTranscriptPerWindow,
                    BudgetWindow);
                return Result.Failure<TranscriptLanguageCoverageDto>(
                    "This transcript has reached its translation limit for today. Try again later.",
                    "RATE_LIMITED");
            }

            var requestId = Guid.NewGuid();
            var published = 0;

            foreach (var batch in Chunk(work.Missing, SegmentsPerRequest))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload = batch
                    .Select(s => new BackfillSegmentPayload(
                        s.Id.ToString(),
                        s.OriginalText!.Trim(),
                        NormalizeLanguage(s.OriginalLanguage),
                        StartMs: s.StartTimeMs,
                        EndMs: s.EndTimeMs))
                    .ToArray();

                await db.StreamAddAsync(
                    RequestStream,
                    RequestEntries(requestId, work.Transcript, work.Language, markerKey, userId, payload),
                    maxLength: 10000,
                    useApproximateMaxLength: true);

                published++;
            }

            _logger.LogInformation(
                "Queued {Segments} segments in {Batches} batches to backfill transcript {TranscriptId} into {Language} (request {RequestId})",
                work.Missing.Count,
                published,
                transcriptId,
                work.Language,
                requestId);

            return Result.Success(await DescribeAsync(work, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing language backfill for transcript {TranscriptId}", transcriptId);

            // A marker claimed a moment ago and then abandoned would report a run that is not
            // happening until its TTL expires. Say what actually became of it instead.
            await TryMarkFailedAsync(transcriptId, targetLanguage);

            return Result.Failure<TranscriptLanguageCoverageDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    private async Task<Result<BackfillWork>> LoadAsync(
        Guid transcriptId,
        Guid userId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var language = NormalizeLanguage(targetLanguage);
        if (language.Length == 0)
        {
            return Result.Failure<BackfillWork>("A target language is required.", "VALIDATION_ERROR");
        }

        var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
        if (transcript == null || transcript.DeletedAt != null)
        {
            return Result.Failure<BackfillWork>($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");
        }

        if (!await _readAccess.CanReadRoomTranscriptAsync(transcript.TranslationRoomId, userId, cancellationToken))
        {
            return Result.Failure<BackfillWork>("You do not have access to this transcript.", "FORBIDDEN");
        }

        var segments = (await _unitOfWork.TranscriptSegments.FindAsync(
                s => s.TranscriptId == transcriptId, cancellationToken))
            .Where(IsTranslatableSegment)
            .ToList();

        var spokenInTarget = segments
            .Where(s => string.Equals(NormalizeLanguage(s.OriginalLanguage), language, StringComparison.Ordinal))
            .Select(s => s.Id)
            .ToHashSet();

        var candidateIds = segments.Select(s => s.Id).ToList();
        var translatedIds = (await _unitOfWork.SegmentTranslationLinks.FindAsync(
                l => candidateIds.Contains(l.SegmentId) && l.IsCurrent && l.TargetLanguage == language,
                cancellationToken))
            .Select(l => l.SegmentId)
            .ToHashSet();

        var missing = segments
            .Where(s => !spokenInTarget.Contains(s.Id) && !translatedIds.Contains(s.Id))
            .OrderBy(s => s.SequenceOrder)
            .ToList();

        return Result.Success(new BackfillWork(
            transcript,
            language,
            segments.Count,
            spokenInTarget.Count,
            translatedIds.Count,
            missing));
    }

    private async Task<TranscriptLanguageCoverageDto> DescribeAsync(
        BackfillWork work,
        CancellationToken cancellationToken)
    {
        var status = await ReadStatusAsync(work, cancellationToken);

        return new TranscriptLanguageCoverageDto(
            work.Language,
            work.TotalSegments,
            work.SpokenInTarget,
            work.Translated,
            work.Missing.Count,
            status);
    }

    private async Task<string> ReadStatusAsync(BackfillWork work, CancellationToken cancellationToken)
    {
        // Nothing left to translate outranks whatever the marker says: a run whose last batch
        // landed a moment ago is finished even though its marker has minutes of TTL left, and
        // reporting "running" there would leave the reader watching a progress bar that is
        // already full.
        if (work.Missing.Count == 0)
        {
            return work.TotalSegments == 0 ? StatusIdle : StatusComplete;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var marker = await _redis.GetDatabase().StringGetAsync(RunMarkerKey(work.Transcript.Id, work.Language));
            if (!marker.HasValue)
            {
                return StatusIdle;
            }

            var value = marker.ToString();
            return value == StatusFailed ? StatusFailed : StatusRunning;
        }
        catch (Exception ex)
        {
            // Redis being unreachable must not turn a coverage read into a 500 — the counts are
            // the answer, the marker only says whether someone is already working on it.
            _logger.LogWarning(ex, "Could not read the backfill marker for transcript {TranscriptId}", work.Transcript.Id);
            return StatusIdle;
        }
    }

    public async Task<int> RequestRetranslationAsync(
        Guid segmentId,
        Guid requestedByUserId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var segment = await _unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (segment == null || !IsTranslatableSegment(segment))
            {
                return 0;
            }

            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(segment.TranscriptId, cancellationToken);
            if (transcript == null)
            {
                return 0;
            }

            // Only the CURRENT link per language. A superseded one describes text nothing serves,
            // and redoing it would spend a translation to replace a row already replaced.
            var links = (await _unitOfWork.SegmentTranslationLinks.FindAsync(
                    l => l.SegmentId == segmentId && l.IsCurrent, cancellationToken))
                .ToList();

            if (links.Count == 0)
            {
                return 0;
            }

            var sourceLanguage = NormalizeLanguage(segment.OriginalLanguage);
            var languages = links
                .Select(l => NormalizeLanguage(l.TargetLanguage))
                .Where(language => language.Length > 0 && language != sourceLanguage)
                .ToHashSet(StringComparer.Ordinal);
            var db = _redis.GetDatabase();

            // Corrections spend the same budget as gap fills: each one redoes every language the
            // line has, and an editor saving the same line over and over is otherwise unbounded
            // spend. Over budget the correction still stands; only its translations stay stale.
            if (languages.Count > 0 && !await TryReserveBudgetAsync(db, transcript.Id, languages.Count))
            {
                _logger.LogWarning(
                    "Skipped retranslating corrected segment {SegmentId}: transcript {TranscriptId} is over its budget of {Budget} per {Window}",
                    segmentId,
                    transcript.Id,
                    MaxQueuedSegmentsPerTranscriptPerWindow,
                    BudgetWindow);
                return 0;
            }
            var requestId = Guid.NewGuid();
            var queued = 0;

            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var language = NormalizeLanguage(link.TargetLanguage);
                if (!languages.Contains(language))
                {
                    continue;
                }

                var payload = new[]
                {
                    new BackfillSegmentPayload(
                        segmentId.ToString(),
                        segment.OriginalText!.Trim(),
                        sourceLanguage,
                        link.TranslationContentId.ToString(),
                        segment.StartTimeMs,
                        segment.EndTimeMs),
                };

                // No status key. The run marker drives the reader's "translating the rest of this
                // meeting" progress bar, and one corrected line is not that — it would show a bar
                // over a count that never moves, because nothing about this line is missing.
                await db.StreamAddAsync(
                    RequestStream,
                    RequestEntries(requestId, transcript, language, markerKey: string.Empty, requestedByUserId, payload),
                    maxLength: 10000,
                    useApproximateMaxLength: true);

                queued++;
            }

            _logger.LogInformation(
                "Queued a retranslation of segment {SegmentId} into {Languages} language(s) after a correction (request {RequestId})",
                segmentId,
                queued,
                requestId);

            return queued;
        }
        catch (Exception ex)
        {
            // The correction itself is already committed. Losing the retranslation leaves the
            // transcript right and its translations stale, which is exactly the state that existed
            // before this path was wired — a bad outcome, and not one worth failing a saved edit over.
            _logger.LogError(ex, "Could not queue a retranslation for segment {SegmentId}", segmentId);
            return 0;
        }
    }

    private static NameValueEntry[] RequestEntries(
        Guid requestId,
        Transcript transcript,
        string targetLanguage,
        string markerKey,
        Guid requestedByUserId,
        IReadOnlyList<BackfillSegmentPayload> payload) =>
        [
            new NameValueEntry("request_id", requestId.ToString()),
            new NameValueEntry("transcript_id", transcript.Id.ToString()),
            // The consumer resolves a room from the payload, so the worker has to be able to echo
            // one back — see TranscriptConsumerPollingPolicy.TryResolveRoomId.
            new NameValueEntry("meeting_id", transcript.TranslationRoomId.ToString()),
            // billing_worker charges the workspace named HERE, not the one the room projection live
            // settlement reads: that projection lives 24h, and a transcript is read back for months.
            new NameValueEntry("workspace_id", transcript.WorkspaceId.ToString()),
            // The usage record's user. A backfilled line has no speaker who spent anything; the
            // reader who picked the language, or the editor who corrected the line, did.
            new NameValueEntry("requested_by_user_id", requestedByUserId.ToString()),
            new NameValueEntry("target_lang", targetLanguage),
            new NameValueEntry("status_key", markerKey),
            new NameValueEntry("segments_json", JsonSerializer.Serialize(payload)),
            new NameValueEntry(
                "timestamp_ms",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
        ];

    /// <summary>
    /// Adds <paramref name="segments"/> to the transcript's window counter, or leaves it unchanged
    /// and returns false when that would cross the budget. INCRBY-then-compensate rather than
    /// read-then-write, so two concurrent requests cannot both read "under" and both queue.
    /// </summary>
    private static async Task<bool> TryReserveBudgetAsync(IDatabase db, Guid transcriptId, int segments)
    {
        var key = BudgetKey(transcriptId);
        var used = await db.StringIncrementAsync(key, segments);

        // A fixed window from first use: only set when the key has no TTL yet, so later
        // reservations do not keep sliding it forward and turn a daily budget into a permanent one.
        await db.KeyExpireAsync(key, BudgetWindow, ExpireWhen.HasNoExpiry);

        if (used <= MaxQueuedSegmentsPerTranscriptPerWindow)
        {
            return true;
        }

        await db.StringDecrementAsync(key, segments);
        return false;
    }

    private async Task TryMarkFailedAsync(Guid transcriptId, string targetLanguage)
    {
        try
        {
            var key = RunMarkerKey(transcriptId, targetLanguage);
            await _redis.GetDatabase().StringSetAsync(key, StatusFailed, RunMarkerTtl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mark the backfill failed for transcript {TranscriptId}", transcriptId);
        }
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var index = 0; index < source.Count; index += size)
        {
            yield return source.Skip(index).Take(size).ToList();
        }
    }

    private sealed record BackfillWork(
        Transcript Transcript,
        string Language,
        int TotalSegments,
        int SpokenInTarget,
        int Translated,
        List<TranscriptSegment> Missing);

    /// <summary>
    /// Mirrors <c>BackfillSegment</c> in warptalk-ai/translation_worker/backfill_worker.py. The
    /// names are snake_case on purpose — the worker reads this JSON verbatim.
    /// </summary>
    private sealed record BackfillSegmentPayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("segment_id")] string SegmentId,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
        [property: System.Text.Json.Serialization.JsonPropertyName("source_lang")] string SourceLang,
        /// <summary>
        /// The translation_contents row this line already has in the target language, when the
        /// line is being redone after a correction. Empty for an ordinary gap fill — the worker
        /// reads its presence as "this is a retranslation".
        /// </summary>
        [property: System.Text.Json.Serialization.JsonPropertyName("previous_translation_content_id")] string PreviousTranslationContentId = "",
        /// <summary>
        /// The line's audio span. Billing prices a backfilled translation exactly like a live one,
        /// TRANSLATION per second of source speech, and this is where the seconds come from.
        /// </summary>
        [property: System.Text.Json.Serialization.JsonPropertyName("start_ms")] int StartMs = 0,
        [property: System.Text.Json.Serialization.JsonPropertyName("end_ms")] int EndMs = 0);
}
