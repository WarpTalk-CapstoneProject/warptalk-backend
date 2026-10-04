using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using WarpTalk.Gateway.Constants;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Background service that consumes AI pipeline results from Redis Streams
/// and pushes them to connected clients via SignalR.
///
/// Streams consumed per active translationRoom:
///   - stt:results:{translationRoomId}     → TranscriptSegmentReceived (original transcript,
///                                           plus WT-716 tier-1 cleanText/cleanFlags)
///   - transcript:clean                    → TranscriptCleanSentenceReceived (WT-716 tier 2)
///   - stt:far_speaker_late                → TranscriptSegmentSpeakerNamed (a bridge stand-in line's
///                                           name, a second after the line itself)
///   - tts:results:{translationRoomId}     → TranslatedAudioReceived (translated + cloned voice) 
///   - ai_assistant:results:{translationRoomId} → AiAssistantResult (summaries, action items)
///                                              → AiSuggestionReceived when type="suggestion"
///
/// Design: AI Assistant runs on its own consumer group on stt:results,
/// completely isolated from the Translation → TTS pipeline.
///
/// WT-605: nothing here is gated on Pause Transcript. This consumer group is the CAPTION lane — it
/// broadcasts to the room and writes nothing down — so a pause must not touch it. The record lane
/// is TranscriptService's own consumer group, which skips paused segments before persisting; the
/// web client keeps paused segments out of its transcript store and sends them to captions only.
/// Gating stt:results here once froze live captions for any room that had not started translation,
/// because this lane is then the captions' only source.
/// </summary>
public sealed class AiResultConsumerService : BackgroundService
{
    private readonly RedisStreamService _streamService;
    private readonly ActiveTranslationRoomRegistry _translationRoomRegistry;
    private readonly IHubContext<TranslationRoomHub> _hubContext;
    private readonly WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient _roomClient;
    private readonly ILogger<AiResultConsumerService> _logger;
    // Live text arrives on pub/sub, which every replica receives; only the relay leader forwards
    // it. Both are optional so a test that does not exercise live text need not supply them.
    private readonly IPubSubLeadership? _relayLeadership;
    private readonly IConnectionMultiplexer? _redis;

    // How long a caption-carrying loop waits after finding its stream empty. RedisStreamService
    // cannot block (StackExchange.Redis has no XREADGROUP BLOCK on a shared connection), so this IS
    // the delivery latency floor for those loops: it was 200ms, half of which every caption paid on
    // average. Measured 4 Oct 2026 as part of the caption budget. The other loops keep 200ms.
    private static readonly TimeSpan CaptionPollDelay = TimeSpan.FromMilliseconds(25);

    private const string ConsumerGroupName = "gateway-consumers";
    private readonly string _consumerName = $"gateway-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";

    // Cache workspace-derived AI policy per translationRoomId. Resolving it costs two gRPC
    // hops (room -> workspace -> settings), so it is fetched once per room and reused by
    // every consumer loop below.
    private readonly ConcurrentDictionary<string, RoomAiPolicy> _roomPolicyCache = new();

    /// <summary>Workspace settings that govern what the AI pipeline may do in one room.</summary>
    private sealed record RoomAiPolicy(bool IsProfanityFilterEnabled, bool AllowExternalLlm);

    // Long enough to outlive any realistic meeting, so suggestion_worker never loses the
    // policy mid-session, and short enough that a stale room's key expires on its own.
    private static readonly TimeSpan AiPolicyTtl = TimeSpan.FromHours(4);

    // translationRoomId → CancellationTokenSource (for stopping consumers when translationRoom ends)
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _translationRoomCts = new();

    /// <summary>
    /// "{translationRoomId}:{speakerId}" → the speaker's display name. WT-534.
    ///
    /// A name does not change during a meeting, so the Redis read happens once per speaker per
    /// room rather than once per sentence. Same unbounded-per-room shape as _roomPolicyCache
    /// above, and bounded in practice by rooms × participants.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _speakerNameCache = new();

    private readonly MeetingCaptionMetrics _captionMetrics;

    /// <summary>
    /// How sure stt_worker must be of a live far-side speaker name before a bridge stand-in line
    /// carries it — <see cref="WarpTalk.Shared.FarSpeakerNames.MinConfidenceConfigKey"/>, the same
    /// key TranscriptService reads, so the live line and the saved row agree.
    /// </summary>
    private readonly double _farSpeakerMinConfidence;

    // ── Pending-list hygiene (WarpTalkAiPendingStuck) ────────
    //
    // Every stream this service reads, for the housekeeping pass. Kept in step with the consume
    // loops by StalePendingHousekeepingTests.
    public static readonly IReadOnlyList<string> ConsumedStreams =
    [
        "stt:results",
        "transcript:clean",
        "translate:results",
        "tts:results",
        "voice:clone:state",
        "ai_assistant:results",
        WarpTalk.Shared.FarSpeakerNames.LateNameStream,
    ];

    // Everything here is a live broadcast: a caption or a summary minutes old is worthless to the
    // room, so an entry pending this long is retired, not redelivered.
    private static readonly TimeSpan StalePendingAge = TimeSpan.FromMinutes(5);

    // A live consumer polls several times a second, so an hour of silence means its process is
    // gone. Deliberately far above anything a Redis blip or a slow deploy could produce.
    private static readonly TimeSpan DeadConsumerIdle = TimeSpan.FromHours(1);

    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromMinutes(5);

    // Lets the consume loops create their groups first, so the first pass does not trip NOGROUP.
    private static readonly TimeSpan HousekeepingStartDelay = TimeSpan.FromSeconds(30);

    // Unroutable entries are logged at Warning at most once a minute per stream, Debug otherwise:
    // a producer that drops meeting_id on stt:results would otherwise log every sentence spoken.
    private static readonly long UnroutableWarningIntervalMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
    private readonly ConcurrentDictionary<string, long> _unroutableWarnedAt = new();

    public AiResultConsumerService(
        RedisStreamService streamService,
        ActiveTranslationRoomRegistry translationRoomRegistry,
        IHubContext<TranslationRoomHub> hubContext,
        WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient workspaceClient,
        WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient roomClient,
        ILogger<AiResultConsumerService> logger,
        IConfiguration? configuration = null,
        IPubSubLeadership? relayLeadership = null,
        IConnectionMultiplexer? redis = null)
    {
        _relayLeadership = relayLeadership;
        _redis = redis;
        _farSpeakerMinConfidence = WarpTalk.Shared.FarSpeakerNames.NormalizeMinConfidence(
            configuration?.GetValue<double?>(WarpTalk.Shared.FarSpeakerNames.MinConfidenceConfigKey));
        _streamService = streamService;
        _translationRoomRegistry = translationRoomRegistry;
        _hubContext = hubContext;
        _workspaceClient = workspaceClient;
        _roomClient = roomClient;
        _logger = logger;
        _captionMetrics = new MeetingCaptionMetrics(streamService, logger);
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AiResultConsumerService starting, consumer={Consumer}", _consumerName);

        try
        {
            // Keep all consumer loops owned by the host so initialization/runtime failures are
            // observed and shutdown awaits every loop instead of leaving unobserved tasks behind.
            await Task.WhenAll(
                ConsumeSTTResultsAsync(stoppingToken),
                ConsumeTranslationResultsAsync(stoppingToken),
                ConsumeTTSResultsAsync(stoppingToken),
                ConsumeAiAssistantResultsAsync(stoppingToken),
                ConsumeCleanSentencesAsync(stoppingToken),
                ConsumeVoiceCloneStateAsync(stoppingToken),
                ConsumeFarSpeakerLateNamesAsync(stoppingToken),
                RelayInterimTranscriptsAsync(stoppingToken),
                HousekeepConsumerGroupsAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
        finally
        {
            _logger.LogInformation("AiResultConsumerService stopped");
        }
    }



    /// <summary>
    /// Creates the consumer group for <paramref name="streamKey"/>, retrying with bounded
    /// backoff until it succeeds or the host stops.
    ///
    /// GUARDED: this used to be a bare call at the top of each consumer loop, outside every
    /// try. Redis Streams was missed by the earlier pub/sub sweep, so an unreachable Redis
    /// threw XGROUP straight out of <see cref="ExecuteAsync"/> (whose only catch is
    /// OperationCanceledException), tripped the default
    /// BackgroundServiceExceptionBehavior.StopHost and took the whole gateway down — YARP
    /// proxying and SignalR included. The app and infra roles deploy in parallel, so reaching
    /// this line before Redis accepts connections is routine.
    ///
    /// Retries rather than giving up so the consumer starts on its own once Redis returns;
    /// same bounded-backoff shape as TranslationRoomRedisSubscriberService.
    /// </summary>
    /// <returns>true once the group exists; false only when the host is shutting down.</returns>
    private async Task<bool> EnsureConsumerGroupWithRetryAsync(string streamKey, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _streamService.EnsureConsumerGroupAsync(streamKey, ConsumerGroupName);
                if (attempt > 0)
                {
                    _logger.LogInformation(
                        "Consumer group {Group} on {Stream} is ready after {Attempts} failed attempt(s); resuming delivery.",
                        ConsumerGroupName, streamKey, attempt);
                }
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogError(
                    ex,
                    "Could not create consumer group {Group} on {Stream} (attempt {Attempt}); retrying in {RetryDelay}. "
                    + "AI pipeline results from this stream are NOT reaching clients until it succeeds.",
                    ConsumerGroupName, streamKey, attempt, retryDelay);

                try
                {
                    await Task.Delay(retryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        return false;
    }

    /// <summary>
    /// Acknowledge an entry no room can receive (no meeting_id, or a payload the client cannot
    /// render), and say so.
    /// </summary>
    /// <remarks>
    /// WarpTalkAiPendingStuck. Four loops used to <c>continue</c> past such an entry without
    /// XACK, so it stayed pending on this consumer forever — and, once the pod was replaced, on a
    /// consumer nobody would ever read as again. On 30 Sep ai_assistant:results held 62 of them
    /// (summaries and action items published without a meeting_id) across 12 dead pods. Retrying
    /// is pointless: an entry with no room can never become routable.
    /// </remarks>
    private async Task AcknowledgeUnroutableAsync(string streamKey, StreamEntry entry)
    {
        await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());

        var now = Environment.TickCount64;
        var warn = !_unroutableWarnedAt.TryGetValue(streamKey, out var last)
            || now - last >= UnroutableWarningIntervalMs;
        if (warn) _unroutableWarnedAt[streamKey] = now;

        _logger.Log(
            warn ? LogLevel.Warning : LogLevel.Debug,
            "Acknowledged unroutable entry {EntryId} on {Stream} (type={Type}) without delivering it: "
            + "no meeting_id or no usable payload, so no room can receive it.",
            entry.Id.ToString(), streamKey, RedisStreamService.GetField(entry, "type") ?? "(none)");
    }

    /// <summary>
    /// Periodically retire stale pending entries and remove dead consumers from this service's
    /// group on every stream it reads. WarpTalkAiPendingStuck.
    /// </summary>
    /// <remarks>
    /// Acking every entry in the loops is not enough on its own: an exception mid-entry (a
    /// SignalR send, say) or a pod killed between read and XACK still leaves entries pending on a
    /// consumer that never comes back, because each process reads under a fresh random name and
    /// nothing reclaims another's. The group also collected one consumer per process ever started
    /// (97 on 30 Sep). Both are cleaned here.
    ///
    /// Runs on every replica with no leader: a claim resets the idle time of what it claims, XACK
    /// is idempotent, and a consumer idle for an hour with nothing pending is dead on any replica's
    /// view. A failure only logs — like EnsureConsumerGroupWithRetryAsync, nothing here may throw
    /// out of ExecuteAsync and stop the host — and the next pass tries again.
    /// </remarks>
    private async Task HousekeepConsumerGroupsAsync(CancellationToken ct)
    {
        await Task.Delay(HousekeepingStartDelay, ct);

        while (!ct.IsCancellationRequested)
        {
            foreach (var streamKey in ConsumedStreams)
            {
                await HousekeepConsumerGroupAsync(streamKey);
            }

            await Task.Delay(HousekeepingInterval, ct);
        }
    }

    private async Task HousekeepConsumerGroupAsync(string streamKey)
    {
        try
        {
            // Claim first: it moves stale entries off dead consumers, which is what lets the
            // deletion below remove them in the same pass.
            var retired = await _streamService.AcknowledgeStalePendingAsync(
                streamKey, ConsumerGroupName, _consumerName, StalePendingAge);
            if (retired > 0)
            {
                _logger.LogWarning(
                    "Retired {Count} entries pending longer than {Age} on {Stream}/{Group} without delivering them.",
                    retired, StalePendingAge, streamKey, ConsumerGroupName);
            }

            var removed = await _streamService.DeleteDeadConsumersAsync(
                streamKey, ConsumerGroupName, _consumerName, DeadConsumerIdle);
            if (removed.Count > 0)
            {
                _logger.LogInformation(
                    "Removed {Count} dead consumer(s) idle longer than {Idle} from {Stream}/{Group}.",
                    removed.Count, DeadConsumerIdle, streamKey, ConsumerGroupName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Pending-list housekeeping failed on {Stream}/{Group}; retrying next pass.",
                streamKey, ConsumerGroupName);
        }
    }

    // ── Profanity Masking ────────────────────────────────────

    private async Task<bool> IsProfanityFilterEnabledAsync(string translationRoomId, CancellationToken ct)
        => (await ResolveRoomPolicyAsync(translationRoomId, ct)).IsProfanityFilterEnabled;

    /// <summary>
    /// Resolve (and cache) the workspace settings that govern this room's AI behaviour,
    /// then project the parts the Python workers need into Redis.
    ///
    /// The projection exists because suggestion_worker must decide whether it may send
    /// transcript text to an external LLM *before* it calls one, and it has no gRPC client
    /// or service credentials to ask WorkspaceService itself. This gateway already makes
    /// the same two-hop lookup for profanity masking, so publishing the answer is nearly
    /// free — and it is written on the FIRST result of a room, before any suggestion could
    /// realistically fire (a suggestion needs several segments of context first).
    ///
    /// On failure this mirrors the pre-existing behaviour for profanity — default to
    /// "no masking" — but deliberately does NOT publish an allow-external-LLM key, so a
    /// WorkspaceService outage leaves suggestion_worker silent rather than sending
    /// transcript text to a provider a workspace may have opted out of.
    /// </summary>
    private async Task<RoomAiPolicy> ResolveRoomPolicyAsync(string translationRoomId, CancellationToken ct)
    {
        if (_roomPolicyCache.TryGetValue(translationRoomId, out var cached))
            return cached;

        RoomAiPolicy policy;
        try
        {
            var roomResponse = await _roomClient.GetTranslationRoomByIdAsync(
                new WarpTalk.Shared.Protos.GetTranslationRoomRequest { Id = translationRoomId }, cancellationToken: ct);

            var workspaceResponse = await _workspaceClient.GetWorkspaceSettingsAsync(
                new WarpTalk.Shared.Protos.GetWorkspaceSettingsRequest { WorkspaceId = roomResponse.WorkspaceId }, cancellationToken: ct);

            policy = new RoomAiPolicy(
                workspaceResponse.IsProfanityFilterEnabled,
                workspaceResponse.AllowExternalLlm);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // A room or workspace that no longer exists has no policy to honour, and no
            // AI work should be started on its behalf.
            policy = new RoomAiPolicy(IsProfanityFilterEnabled: false, AllowExternalLlm: false);
            _roomPolicyCache.TryAdd(translationRoomId, policy);
            return policy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve AI policy for room {RoomId}", translationRoomId);
            // Not cached: a transient WorkspaceService failure must not pin this room to a
            // fail-closed policy for the rest of the process's life.
            return new RoomAiPolicy(IsProfanityFilterEnabled: false, AllowExternalLlm: false);
        }

        _roomPolicyCache.TryAdd(translationRoomId, policy);
        await PublishAiPolicyAsync(translationRoomId, policy, ct);
        return policy;
    }

    private async Task PublishAiPolicyAsync(string translationRoomId, RoomAiPolicy policy, CancellationToken ct)
    {
        try
        {
            await _streamService.SetWithTtlAsync(
                $"translationRoom:{translationRoomId}:ai_policy",
                JsonSerializer.Serialize(new { allow_external_llm = policy.AllowExternalLlm }),
                AiPolicyTtl);
        }
        catch (Exception ex)
        {
            // Never let this break result delivery — the workers reading it fail closed on
            // a missing key, which is the safe outcome.
            _logger.LogWarning(ex, "Failed to publish AI policy for room {RoomId}", translationRoomId);
        }
    }

    /// <summary>
    /// Whether this failure is a consumer group that no longer exists — and if so, put it back.
    /// </summary>
    /// <remarks>
    /// WT-387. EnsureConsumerGroupWithRetryAsync runs ONCE, before each consume loop starts. A
    /// consumer group lives inside its stream, so deleting the stream deletes the group with it,
    /// and from then on every XREADGROUP answers NOGROUP. That landed in the loop's generic catch,
    /// which logged and slept — forever. The pipeline was dead for the life of the process while
    /// the service went on reporting healthy, which is exactly the report: live transcript stops
    /// mid-meeting and never resumes.
    ///
    /// Two things delete a stream, and BOTH are in production today:
    ///
    ///   * REDIS_STREAM_TTL_SECONDS=3600, added as the mitigation for this very incident. Any
    ///     stream that goes quiet for an hour expires, which makes this reachable on an ordinary
    ///     idle night rather than only under memory pressure.
    ///   * maxmemory-policy allkeys-lru, which is what deleted live meetings' streams on
    ///     2026-08-14 to make room (see deploy/production/app.compose.yml).
    ///
    /// Recreating is safe and cheap: EnsureConsumerGroupAsync passes createStream:true and
    /// swallows BUSYGROUP, so it is a no-op when the group is already there. Messages published
    /// while the stream did not exist are genuinely gone — nothing can recover those — but the
    /// consumer resumes instead of staying dead until somebody restarts the gateway.
    /// </remarks>
    private async Task<bool> TryRestoreConsumerGroupAsync(Exception ex, string streamKey, CancellationToken ct)
    {
        // Matched on the message: StackExchange.Redis surfaces this as a RedisServerException
        // whose text begins "NOGROUP No such key '…' or consumer group '…'", and there is no
        // typed error to test instead.
        if (ex is not RedisServerException || !ex.Message.Contains("NOGROUP", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _logger.LogWarning(
            ex,
            "Consumer group {Group} on {Stream} has vanished — the stream was deleted (TTL expiry or eviction). "
            + "Recreating it; messages published while it was gone are lost.",
            ConsumerGroupName, streamKey);

        return await EnsureConsumerGroupWithRetryAsync(streamKey, ct);
    }

    // ── STT Results → TranscriptSegmentReceived ──────────────

    private async Task ConsumeSTTResultsAsync(CancellationToken ct)
    {
        var streamKey = "stt:results";

        if (!await EnsureConsumerGroupWithRetryAsync(streamKey, ct))
            return;

        _logger.LogDebug("Consuming STT results: {StreamKey}", streamKey);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ConsumeAsync(
                    streamKey, ConsumerGroupName, _consumerName, count: 10, blockMs: 2000);

                foreach (var entry in entries)
                {
                    var translationRoomId = RedisStreamService.GetField(entry, "meeting_id") ?? "";
                    if (string.IsNullOrEmpty(translationRoomId))
                    {
                        await AcknowledgeUnroutableAsync(streamKey, entry);
                        continue;
                    }

                    var originalText = RedisStreamService.GetField(entry, "text") ?? "";
                    // WT-716 tier 1. Masked under the same switch as the raw text: Clean is the
                    // DEFAULT view, so an unmasked clean line would be the profanity filter off for
                    // most readers.
                    var cleanText = TryReadCleanText(entry);

                    if (await IsProfanityFilterEnabledAsync(translationRoomId, ct))
                    {
                        originalText = WarpTalk.Gateway.Helpers.ProfanityFilterHelper.MaskProfanity(originalText);
                        cleanText = cleanText is null
                            ? null
                            : WarpTalk.Gateway.Helpers.ProfanityFilterHelper.MaskProfanity(cleanText);
                    }

                    var speakerId = RedisStreamService.GetField(entry, "speaker_id") ?? "";

                    var segment = new TranscriptSegmentDto(
                        SegmentId: Guid.TryParse(RedisStreamService.GetField(entry, "segment_id"), out var sid) ? sid : Guid.NewGuid(),
                        SpeakerId: Guid.TryParse(speakerId, out var spk) ? spk : Guid.Empty,
                        // WT-534: a name, when one is known. This field carried the speaker's UUID
                        // — the id, put in the field called Name — so the live transcript had no
                        // name in it at all. The web client guards against printing a UUID at
                        // somebody, so every line whose speaker was not already in the reader's
                        // roster rendered as the literal word "Speaker", and a reader watching a
                        // rejoin saw attribution they could not trust. The SAVED transcript never
                        // had this problem: TranscriptRedisConsumerService resolves the name over
                        // auth gRPC before it writes the row, so the two copies of the same
                        // meeting disagreed about who spoke.
                        //
                        // A bridge stand-in line is not looked up at all: its LiveKit name is the
                        // seat ("External Meeting"), not whoever spoke. It is named from the live
                        // far-side hint on the entry, by the rule the saved row uses.
                        SpeakerName: TryResolveStandInSpeakerName(entry, _farSpeakerMinConfidence)
                            ?? await ResolveSpeakerNameAsync(translationRoomId, speakerId),
                        OriginalText: originalText,
                        OriginalLanguage: RedisStreamService.GetField(entry, "language") ?? "unknown",
                        TranslatedText: null,
                        TargetLanguage: null,
                        Confidence: TryReadSttConfidence(entry),
                        StartTimeMs: int.TryParse(RedisStreamService.GetField(entry, "start_ms"), out var start) ? start : 0,
                        EndTimeMs: int.TryParse(RedisStreamService.GetField(entry, "end_ms"), out var end) ? end : 0,
                        CleanText: cleanText,
                        CleanFlags: ReadFlags(RedisStreamService.GetField(entry, "clean_flags")));

                    await _hubContext.Clients
                        .Group($"translationRoom:{translationRoomId}")
                        .SendAsync("TranscriptSegmentReceived", segment, ct);

                    MeetingCaptionMetrics.RecordDelivered(MeetingCaptionMetrics.KindTranscript);
                    await _captionMetrics.MarkFirstCaptionAsync(translationRoomId);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(CaptionPollDelay, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // WT-387: a vanished consumer group is recoverable; everything else is not.
                if (await TryRestoreConsumerGroupAsync(ex, streamKey, ct)) continue;
                _logger.LogError(ex, "Error consuming STT results");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// The live name of a bridge stand-in line, or <c>null</c> when the entry's speaker is not the
    /// stand-in (<see cref="WarpTalk.Shared.ExternalBridgeConstants.ParticipantUserId"/>).
    /// </summary>
    /// <remarks>
    /// <c>far_speaker_name</c> when stt_worker attached one with <c>far_speaker_confidence</c> at
    /// or above <paramref name="minConfidence"/>, "Google Meet participants" otherwise — see
    /// <see cref="WarpTalk.Shared.FarSpeakerNames"/>, which TranscriptService applies to the saved
    /// row too. Pure and static so the rule is testable without a running consumer.
    /// </remarks>
    public static string? TryResolveStandInSpeakerName(StreamEntry entry, double minConfidence)
    {
        if (!Guid.TryParse(RedisStreamService.GetField(entry, "speaker_id"), out var speaker)
            || speaker != WarpTalk.Shared.ExternalBridgeConstants.ParticipantUserId)
        {
            return null;
        }

        return WarpTalk.Shared.FarSpeakerNames.ResolveLive(
            RedisStreamService.GetField(entry, "far_speaker_name"),
            WarpTalk.Shared.FarSpeakerNames.ParseConfidence(RedisStreamService.GetField(entry, "far_speaker_confidence")),
            minConfidence);
    }

    // ── Live text → TranscriptInterimReceived ─────────────────

    /// <summary>
    /// Relays stt_worker's live text — the words of a turn still being spoken — to the room.
    ///
    /// Measured 4 Oct 2026: a short sentence reached the caption ~2.5s after the speaker stopped,
    /// because nothing was shown until the turn closed. The model already had the words ~1s behind
    /// the speaker. This forwards them as they come; the TranscriptSegmentReceived line for the same
    /// speaker replaces them.
    ///
    /// Masked under the same profanity switch as the final line, or the filter would be off for the
    /// second or two before every line. Pub/sub reaches every replica, so only the relay leader
    /// sends. Not a required relay subscription on purpose: a failure here must cost live text, not
    /// every other realtime event the leader carries.
    /// </summary>
    private async Task RelayInterimTranscriptsAsync(CancellationToken ct)
    {
        if (_redis is null || _relayLeadership is null)
            return;

        var subscriber = _redis.GetSubscriber();
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(
                    RedisChannel.Literal(RealtimeConstants.RedisChannels.SttInterim),
                    (_channel, message) => _ = RelayInterimAsync(message, ct));
                _logger.LogInformation("Relaying live text from '{Channel}'.", RealtimeConstants.RedisChannels.SttInterim);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not subscribe to live text; retrying in {RetryDelay}.", retryDelay);
                await Task.Delay(retryDelay, ct);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task RelayInterimAsync(RedisValue message, CancellationToken ct)
    {
        try
        {
            if (_relayLeadership is null || !_relayLeadership.ShouldHandle || message.IsNullOrEmpty)
                return;

            var interim = TryReadInterim(message.ToString());
            if (interim is null)
                return;

            var (roomId, speakerId, itemId, text, language) = interim.Value;
            if (await IsProfanityFilterEnabledAsync(roomId, ct))
                text = WarpTalk.Gateway.Helpers.ProfanityFilterHelper.MaskProfanity(text);

            var dto = new TranscriptInterimDto(
                SpeakerId: Guid.TryParse(speakerId, out var spk) ? spk : Guid.Empty,
                SpeakerName: await ResolveSpeakerNameAsync(roomId, speakerId),
                ItemId: itemId,
                Text: text,
                Language: language);

            await _hubContext.Clients
                .Group($"translationRoom:{roomId}")
                .SendAsync("TranscriptInterimReceived", dto, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A dropped preview is replaced by the final line moments later.
            _logger.LogDebug(ex, "Live text relay failed.");
        }
    }

    /// <summary>Parses one stt:interim message, or null when it is not one this relay can route.</summary>
    public static (string RoomId, string SpeakerId, string ItemId, string Text, string Language)? TryReadInterim(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Read(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
            var roomId = Read("meeting_id");
            var text = Read("text");
            if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(text))
                return null;
            return (roomId, Read("speaker_id"), Read("item_id"), text, Read("language"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Late far-side names → TranscriptSegmentSpeakerNamed ──

    /// <summary>
    /// Relays a bridge stand-in line's LATE name to the room: the line already went out as "Google
    /// Meet participants" on TranscriptSegmentReceived, and stt_worker has since become sure who on
    /// the Meet side said it. See <see cref="WarpTalk.Shared.FarSpeakerNames.LateNameStream"/> for
    /// why the name can trail its line, and why it rides a stream of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stateless, like every relay here. The client applies the event to the line with the same
    /// segment id only while that line still reads the fallback, so a duplicate (a redelivery, a
    /// second replica) renames nothing twice, and an id the client never saw is ignored.
    /// </para>
    /// <para>
    /// The same gate as the line itself: the confidence is checked again against
    /// <see cref="_farSpeakerMinConfidence"/>, the threshold <see cref="TryResolveStandInSpeakerName"/>
    /// applies to stt:results. stt_worker only sends at or above ITS 0.6, but this deployment may be
    /// configured stricter, and a late entry must not show a name the line itself would have hidden.
    /// TranscriptService reads the same stream in its own group and applies the same check to the
    /// saved row, so a reload agrees with what the room saw.
    /// </para>
    /// <para>
    /// No ordering against the stt:results loop is attempted. The two loops (and two replicas) can
    /// race, and the late entry could in principle overtake its own line; the client then ignores
    /// it, and the saved row — which TranscriptService names regardless — carries the name on the
    /// next load. The producer publishes ≥ 1 s after the line, so in practice the line is long out.
    /// </para>
    /// <para>
    /// Never logs the name: a Meet participant's name is personal data from somebody else's meeting.
    /// </para>
    /// </remarks>
    private async Task ConsumeFarSpeakerLateNamesAsync(CancellationToken ct)
    {
        var streamKey = "stt:far_speaker_late";

        if (!await EnsureConsumerGroupWithRetryAsync(streamKey, ct))
            return;

        _logger.LogDebug("Consuming late far-side speaker names: {StreamKey}", streamKey);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ConsumeAsync(
                    streamKey, ConsumerGroupName, _consumerName, count: 10, blockMs: 2000);

                foreach (var entry in entries)
                {
                    var translationRoomId = RedisStreamService.GetField(entry, "meeting_id") ?? "";
                    var named = TryReadLateSpeakerName(entry, _farSpeakerMinConfidence);

                    // Acknowledged either way, like every loop here: an entry with no room, no
                    // segment id or no showable name can never become deliverable, and leaving it
                    // pending would only feed WarpTalkAiPendingStuck. A name under this
                    // deployment's threshold lands here too, on purpose — it is not to be shown.
                    if (string.IsNullOrEmpty(translationRoomId) || named is null)
                    {
                        await AcknowledgeUnroutableAsync(streamKey, entry);
                        continue;
                    }

                    await _hubContext.Clients
                        .Group($"translationRoom:{translationRoomId}")
                        .SendAsync("TranscriptSegmentSpeakerNamed", named, ct);

                    _logger.LogDebug(
                        "Relayed a late far-side name for segment {SegmentId} in room {RoomId}",
                        named.SegmentId, translationRoomId);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // WT-387: a vanished consumer group is recoverable; everything else is not.
                if (await TryRestoreConsumerGroupAsync(ex, streamKey, ct)) continue;
                _logger.LogError(ex, "Error consuming late far-side speaker names");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// One <c>stt:far_speaker_late</c> entry as a client payload, or <c>null</c> when it must not be
    /// shown: no parsable <c>segment_id</c>, no name, a confidence that is absent, unparsable or under
    /// <paramref name="minConfidence"/>, or a <c>speaker_id</c> that names somebody other than the
    /// bridge stand-in. Pure and static, like <see cref="TryResolveStandInSpeakerName"/>.
    /// </summary>
    /// <remarks>
    /// The wire contract carries no <c>speaker_id</c> — stt_worker only publishes late names for
    /// stand-in segments. When a producer does add one and it is anybody else, the entry is refused:
    /// a real participant's line is named by who they are, never by a Meet caption.
    /// </remarks>
    public static TranscriptSegmentSpeakerNamedDto? TryReadLateSpeakerName(StreamEntry entry, double minConfidence)
    {
        if (!Guid.TryParse(RedisStreamService.GetField(entry, "segment_id"), out var segmentId)
            || segmentId == Guid.Empty)
        {
            return null;
        }

        var speakerId = RedisStreamService.GetField(entry, "speaker_id");
        if (!string.IsNullOrWhiteSpace(speakerId)
            && (!Guid.TryParse(speakerId, out var speaker)
                || speaker != WarpTalk.Shared.ExternalBridgeConstants.ParticipantUserId))
        {
            return null;
        }

        var name = WarpTalk.Shared.FarSpeakerNames.TryResolveConfident(
            RedisStreamService.GetField(entry, "far_speaker_name"),
            WarpTalk.Shared.FarSpeakerNames.ParseConfidence(RedisStreamService.GetField(entry, "far_speaker_confidence")),
            minConfidence);

        return name is null ? null : new TranscriptSegmentSpeakerNamedDto(segmentId, name);
    }

    /// <summary>
    /// The speaker's display name for the live transcript, falling back to their id. WT-534.
    /// </summary>
    /// <remarks>
    /// Read from <c>meeting:{roomId}:speaker_names</c>, the hash the AI ingress worker fills as it
    /// meets each speaker (WT-529) from the <c>name</c> claim on their LiveKit token. Redis rather
    /// than auth gRPC because this runs once per sentence spoken in every live meeting, and the
    /// ingress worker has already paid for the lookup.
    ///
    /// The id is the fallback, not "Unknown": it is what this field carried before, so nothing
    /// that reads it regresses, and the web client already refuses to print a UUID as a name —
    /// it shows "Speaker" instead, which is honest. Inventing a name here would not be.
    /// </remarks>
    private async Task<string> ResolveSpeakerNameAsync(string translationRoomId, string speakerId)
    {
        if (string.IsNullOrEmpty(speakerId)) return "Unknown";

        var cacheKey = $"{translationRoomId}:{speakerId}";
        if (_speakerNameCache.TryGetValue(cacheKey, out var cached)) return cached;

        try
        {
            var name = await _streamService.GetHashFieldAsync(
                $"meeting:{translationRoomId}:speaker_names", speakerId);

            if (!string.IsNullOrWhiteSpace(name))
            {
                _speakerNameCache[cacheKey] = name;
                return name;
            }
        }
        catch (Exception ex)
        {
            // Best-effort by design. A transcript line with a weaker name is worth delivering;
            // one that never arrives because Redis hiccuped is not.
            _logger.LogDebug(ex, "Speaker name lookup failed for {SpeakerId} in room {RoomId}",
                speakerId, translationRoomId);
        }

        // Deliberately NOT cached: the ingress worker writes the hash as it meets each speaker,
        // so a miss here is usually "not yet", and caching it would freeze the id in place for
        // the rest of the meeting.
        return speakerId;
    }

    // ── Clean sentences → TranscriptCleanSentenceReceived ────

    /// <summary>
    /// WT-716 tier 2: relay each whole cleaned sentence to the room as it is (re)written, so the
    /// live Clean view does not have to wait for the meeting to end and reload.
    ///
    /// Stateless, like every relay here: revisions go out in the order they arrive, and the client
    /// keeps the highest per sentence id — the same rule TranscriptService applies when it stores
    /// them. Not gated on Pause Transcript, for the reason in this class's summary: this is a
    /// display lane, and the web client already keeps paused segments out of its transcript store,
    /// so a sentence naming only those has nothing to attach to.
    /// </summary>
    private async Task ConsumeCleanSentencesAsync(CancellationToken ct)
    {
        var streamKey = "transcript:clean";

        if (!await EnsureConsumerGroupWithRetryAsync(streamKey, ct))
            return;

        _logger.LogDebug("Consuming clean sentences: {StreamKey}", streamKey);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ConsumeAsync(
                    streamKey, ConsumerGroupName, _consumerName, count: 10, blockMs: 2000);

                foreach (var entry in entries)
                {
                    var translationRoomId = RedisStreamService.GetField(entry, "meeting_id") ?? "";
                    var sentence = TryReadCleanSentence(entry);

                    // Acknowledged even when unusable, so a malformed entry cannot wedge the group
                    // for every later sentence of every room. TranscriptService's own consumer
                    // group dead-letters it with its payload; that is where it gets looked at.
                    if (string.IsNullOrEmpty(translationRoomId) || sentence is null)
                    {
                        await AcknowledgeUnroutableAsync(streamKey, entry);
                        continue;
                    }

                    if (await IsProfanityFilterEnabledAsync(translationRoomId, ct))
                    {
                        sentence = sentence with
                        {
                            CleanText = WarpTalk.Gateway.Helpers.ProfanityFilterHelper.MaskProfanity(sentence.CleanText),
                        };
                    }

                    await _hubContext.Clients
                        .Group($"translationRoom:{translationRoomId}")
                        .SendAsync("TranscriptCleanSentenceReceived", sentence, ct);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(CaptionPollDelay, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // WT-387: a vanished consumer group is recoverable; everything else is not.
                if (await TryRestoreConsumerGroupAsync(ex, streamKey, ct)) continue;
                _logger.LogError(ex, "Error consuming clean sentences");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// WT-716 tier 1: the <c>clean_text</c> on an stt:results entry, or <c>null</c> when the field
    /// is absent. Absent ("not cleaned — show the raw text") and empty ("filler only — hide it")
    /// are different answers and are kept apart; same rule as TranscriptService's
    /// TranscriptConsumerPollingPolicy.ResolveCleanText, so the live line and the stored row agree.
    /// </summary>
    public static string? TryReadCleanText(StreamEntry entry)
    {
        foreach (var nv in entry.Values)
        {
            if (nv.Name == "clean_text")
                return nv.Value.IsNull ? string.Empty : nv.Value.ToString();
        }
        return null;
    }

    /// <summary>A comma-separated flag list as a trimmed, de-duplicated array; empty (never null)
    /// when absent. Unknown flags pass through — the producer owns the vocabulary.</summary>
    public static IReadOnlyList<string> ReadFlags(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>
    /// WT-716 tier 2: one <c>transcript:clean</c> entry as a client payload, or <c>null</c> when it
    /// cannot be applied (no sentence id, revision, segment ids or clean_text). Pure and static,
    /// like <see cref="TryReadSuggestion"/>, so it is testable without Redis.
    /// </summary>
    public static TranscriptCleanSentenceDto? TryReadCleanSentence(StreamEntry entry)
    {
        if (!Guid.TryParse(RedisStreamService.GetField(entry, "sentence_id"), out var sentenceId))
            return null;
        if (!int.TryParse(RedisStreamService.GetField(entry, "revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
            || revision < 0)
            return null;

        var cleanText = RedisStreamService.GetField(entry, "clean_text");
        if (cleanText is null)
            return null;

        var segmentIds = TryReadSegmentIds(RedisStreamService.GetField(entry, "segment_ids"));
        if (segmentIds is null)
            return null;

        var language = RedisStreamService.GetField(entry, "language");
        var source = RedisStreamService.GetField(entry, "source");

        return new TranscriptCleanSentenceDto(
            Id: sentenceId,
            SpeakerId: Guid.TryParse(RedisStreamService.GetField(entry, "speaker_id"), out var speaker) ? speaker : null,
            SegmentIds: segmentIds,
            CleanText: cleanText,
            Language: string.IsNullOrWhiteSpace(language) ? "unknown" : language.Trim(),
            Flags: ReadFlags(RedisStreamService.GetField(entry, "flags")),
            Source: string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim(),
            Revision: revision);
    }

    /// <summary>A non-empty JSON array of GUID strings, order kept, duplicates dropped — or null.
    /// One bad element voids the list: a sentence silently missing a segment looks complete.</summary>
    private static IReadOnlyList<Guid>? TryReadSegmentIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var raw = JsonSerializer.Deserialize<string[]>(json);
            if (raw is null || raw.Length == 0)
                return null;

            var ids = new List<Guid>(raw.Length);
            foreach (var item in raw)
            {
                if (!Guid.TryParse(item, out var id))
                    return null;
                if (!ids.Contains(id))
                    ids.Add(id);
            }
            return ids;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Translation Results → TranslationTextReceived ────────

    private async Task ConsumeTranslationResultsAsync(CancellationToken ct)
    {
        var streamKey = "translate:results";

        if (!await EnsureConsumerGroupWithRetryAsync(streamKey, ct))
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ConsumeAsync(
                    streamKey, ConsumerGroupName, _consumerName, count: 10);

                foreach (var entry in entries)
                {
                    var translationRoomId = RedisStreamService.GetField(entry, "meeting_id") ?? "";
                    if (string.IsNullOrEmpty(translationRoomId))
                    {
                        await AcknowledgeUnroutableAsync(streamKey, entry);
                        continue;
                    }

                    var originalText = RedisStreamService.GetField(entry, "original_text") ?? "";
                    var translatedText = RedisStreamService.GetField(entry, "translated_text") ?? "";

                    if (await IsProfanityFilterEnabledAsync(translationRoomId, ct))
                    {
                        originalText = WarpTalk.Gateway.Helpers.ProfanityFilterHelper.MaskProfanity(originalText);
                        translatedText = WarpTalk.Gateway.Helpers.ProfanityFilterHelper.MaskProfanity(translatedText);
                    }

                    var dto = new TranslationTextDto(
                        SegmentId: RedisStreamService.GetField(entry, "segment_id") ?? "",
                        SpeakerId: Guid.TryParse(RedisStreamService.GetField(entry, "speaker_id"), out var spk) ? spk : Guid.Empty,
                        OriginalText: originalText,
                        TranslatedText: translatedText,
                        SourceLang: RedisStreamService.GetField(entry, "source_lang") ?? "",
                        TargetLang: RedisStreamService.GetField(entry, "target_lang") ?? "",
                        StartTimeMs: int.TryParse(RedisStreamService.GetField(entry, "start_ms"), out var tStart) ? tStart : 0,
                        EndTimeMs: int.TryParse(RedisStreamService.GetField(entry, "end_ms"), out var tEnd) ? tEnd : 0,
                        SourceSegmentId: RedisStreamService.GetField(entry, "source_segment_id") ?? "",
                        ChunkIndex: int.TryParse(RedisStreamService.GetField(entry, "chunk_index"), out var chunkIdx) ? chunkIdx : 0);

                    await _hubContext.Clients
                        .Group($"translationRoom:{translationRoomId}")
                        .SendAsync("TranslationTextReceived", dto, ct);

                    MeetingCaptionMetrics.RecordDelivered(MeetingCaptionMetrics.KindTranslation);
                    await _captionMetrics.MarkFirstCaptionAsync(translationRoomId);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(CaptionPollDelay, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // WT-387: a vanished consumer group is recoverable; everything else is not.
                if (await TryRestoreConsumerGroupAsync(ex, streamKey, ct)) continue;
                _logger.LogError(ex, "Error consuming Translation results");
                await Task.Delay(2000, ct);
            }
        }
    }

    // ── TTS Results → TranslatedAudioReceived ────────────────

    private async Task ConsumeTTSResultsAsync(CancellationToken ct)
    {
        var streamKey = "tts:results";

        if (!await EnsureConsumerGroupWithRetryAsync(streamKey, ct))
            return;

        _logger.LogDebug("Consuming TTS results: {StreamKey}", streamKey);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ConsumeAsync(
                    streamKey, ConsumerGroupName, _consumerName, count: 5, blockMs: 2000);

                foreach (var entry in entries)
                {
                    var translationRoomId = RedisStreamService.GetField(entry, "meeting_id") ?? "";
                    if (string.IsNullOrEmpty(translationRoomId))
                    {
                        await AcknowledgeUnroutableAsync(streamKey, entry);
                        continue;
                    }
                    var audioDto = new TranslatedAudioDto(
                        SegmentId: RedisStreamService.GetField(entry, "segment_id") ?? "",
                        SpeakerId: Guid.TryParse(RedisStreamService.GetField(entry, "speaker_id"), out var spk) ? spk : Guid.Empty,
                        AudioBase64: RedisStreamService.GetField(entry, "audio_data") ?? "",
                        VoiceType: RedisStreamService.GetField(entry, "voice_type") ?? "default",
                        DurationMs: int.TryParse(RedisStreamService.GetField(entry, "duration_ms"), out var dur) ? dur : 0,
                        VoiceMode: RedisStreamService.GetField(entry, "voice_mode"),
                        CloneStrength: double.TryParse(RedisStreamService.GetField(entry, "clone_strength"), NumberStyles.Float, CultureInfo.InvariantCulture, out var strength) ? strength : null,
                        AnchorProvider: RedisStreamService.GetField(entry, "anchor_provider"),
                        CloneProvider: RedisStreamService.GetField(entry, "clone_provider"),
                        RenderLocation: RedisStreamService.GetField(entry, "render_location"),
                        CacheKey: RedisStreamService.GetField(entry, "cache_key"),
                        CacheHit: bool.TryParse(RedisStreamService.GetField(entry, "cache_hit"), out var cacheHit) ? cacheHit : null,
                        SynthesisLatencyMs: int.TryParse(RedisStreamService.GetField(entry, "synthesis_latency_ms"), out var synthMs) ? synthMs : null,
                        ConversionLatencyMs: int.TryParse(RedisStreamService.GetField(entry, "conversion_latency_ms"), out var conversionMs) ? conversionMs : null,
                        FallbackReason: RedisStreamService.GetField(entry, "fallback_reason"));

                    await _hubContext.Clients
                        .Group($"translationRoom:{translationRoomId}")
                        .SendAsync("TranslatedAudioReceived", audioDto, ct);

                    MeetingCaptionMetrics.RecordDelivered(MeetingCaptionMetrics.KindAudio);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());

                    _logger.LogDebug(
                        "Delivered TTS audio: translationRoom={TranslationRoomId}, segment={SegmentId}, voice={VoiceType}",
                        translationRoomId, audioDto.SegmentId, audioDto.VoiceType);
                }

                if (entries.Length == 0)
                    await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // WT-387: a vanished consumer group is recoverable; everything else is not.
                if (await TryRestoreConsumerGroupAsync(ex, streamKey, ct)) continue;
                _logger.LogError(ex, "Error consuming TTS results");
                await Task.Delay(1000, ct);
            }
        }
    }

    // ── Voice clone capture state → VoiceCloneStateChanged ───

    /// <summary>
    /// WT-420 — relay what the TTS worker knows about a speaker's voice clone to that speaker.
    ///
    /// The worker has always known whether it is capturing, how far along it is, whether the clip
    /// was accepted and why one was refused. It wrote all of it to a structured log, which is the
    /// wrong audience: on 15 Aug an entire test session concluded cloning was broken while the
    /// worker was logging score 1.0. Nobody in a meeting can read a worker log.
    ///
    /// Broadcast to the room group rather than to one connection: the payload names its own
    /// speaker, the client already filters realtime events by participant, and a per-user send
    /// would need a userId→connection map this service does not have and should not grow.
    /// </summary>
    private async Task ConsumeVoiceCloneStateAsync(CancellationToken ct)
    {
        var streamKey = "voice:clone:state";

        if (!await EnsureConsumerGroupWithRetryAsync(streamKey, ct))
            return;

        _logger.LogDebug("Consuming voice clone state: {StreamKey}", streamKey);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ConsumeAsync(
                    streamKey, ConsumerGroupName, _consumerName, count: 10, blockMs: 2000);

                foreach (var entry in entries)
                {
                    var translationRoomId = RedisStreamService.GetField(entry, "meeting_id") ?? "";
                    var reason = RedisStreamService.GetField(entry, "reason") ?? "";

                    // Acknowledged even when unusable, so a malformed entry cannot wedge the group
                    // and stop every later message for every room.
                    if (string.IsNullOrEmpty(translationRoomId) || string.IsNullOrEmpty(reason))
                    {
                        await AcknowledgeUnroutableAsync(streamKey, entry);
                        continue;
                    }

                    var state = new VoiceCloneStateDto(
                        SpeakerId: RedisStreamService.GetField(entry, "speaker_id") ?? "",
                        Reason: reason,
                        // Absent rather than zero: the worker omits a metric it has nothing to say
                        // about, and a 0.0 would render as "0 seconds captured" or "quality 0",
                        // which is a claim rather than a gap.
                        Seconds: ParseDouble(entry, "seconds"),
                        RequiredSeconds: ParseDouble(entry, "required_seconds"),
                        Score: ParseDouble(entry, "score"),
                        ActiveSpeechRatio: ParseDouble(entry, "active_speech_ratio"));

                    await _hubContext.Clients
                        .Group($"translationRoom:{translationRoomId}")
                        .SendAsync("VoiceCloneStateChanged", state, ct);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // WT-387: a vanished consumer group is recoverable; everything else is not.
                if (await TryRestoreConsumerGroupAsync(ex, streamKey, ct)) continue;
                _logger.LogError(ex, "Error consuming voice clone state");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>InvariantCulture, because the worker writes these with Python's repr and a
    /// comma-decimal server locale would turn 0.978 into 978.</summary>
    private static double? ParseDouble(StackExchange.Redis.StreamEntry entry, string field)
    {
        var raw = RedisStreamService.GetField(entry, field);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    // ── AI Assistant Results → AiAssistantResult ─────────────

    private async Task ConsumeAiAssistantResultsAsync(CancellationToken ct)
    {
        var streamKey = "ai_assistant:results";

        if (!await EnsureConsumerGroupWithRetryAsync(streamKey, ct))
            return;

        _logger.LogDebug("Consuming AI Assistant results: {StreamKey}", streamKey);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ConsumeAsync(
                    streamKey, ConsumerGroupName, _consumerName, count: 5, blockMs: 5000);

                foreach (var entry in entries)
                {
                    var translationRoomId = RedisStreamService.GetField(entry, "meeting_id") ?? "";
                    if (string.IsNullOrEmpty(translationRoomId))
                    {
                        await AcknowledgeUnroutableAsync(streamKey, entry);
                        continue;
                    }

                    // Inline transcript suggestions ride this same stream but are a different
                    // client event with a different shape. Route them out FIRST and leave the
                    // legacy branch below byte-identical: before this split, an unrecognised
                    // `type` still went out as "AiAssistantResult", so a suggestion reaching a
                    // gateway that predates this code would surface inside the summary panel.
                    var suggestion = TryReadSuggestion(entry, translationRoomId);
                    if (suggestion is not null)
                    {
                        if (await IsProfanityFilterEnabledAsync(translationRoomId, ct))
                        {
                            suggestion = suggestion with
                            {
                                Content = WarpTalk.Gateway.Helpers.ProfanityFilterHelper.MaskProfanity(suggestion.Content),
                                Detail = suggestion.Detail is null
                                    ? null
                                    : WarpTalk.Gateway.Helpers.ProfanityFilterHelper.MaskProfanity(suggestion.Detail),
                            };
                        }

                        await _hubContext.Clients
                            .Group($"translationRoom:{translationRoomId}")
                            .SendAsync("AiSuggestionReceived", suggestion, ct);

                        await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                        continue;
                    }

                    var result = new AiAssistantResultDto(
                        TranslationRoomId: translationRoomId,
                        Type: RedisStreamService.GetField(entry, "type") ?? "summary",
                        Content: RedisStreamService.GetField(entry, "content") ?? "",
                        CreatedAt: DateTime.UtcNow);

                    await _hubContext.Clients
                        .Group($"translationRoom:{translationRoomId}")
                        .SendAsync("AiAssistantResult", result, ct);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // WT-387: a vanished consumer group is recoverable; everything else is not.
                if (await TryRestoreConsumerGroupAsync(ex, streamKey, ct)) continue;
                _logger.LogError(ex, "Error consuming AI Assistant results");
                await Task.Delay(2000, ct);
            }
        }
    }

    /// <summary>
    /// Reads an ai_assistant:results entry as a suggestion, or returns null when the entry
    /// is anything else (a summary, action items, a future message type) so the caller falls
    /// through to the legacy AiAssistantResult path.
    ///
    /// Pure and static so the routing rule is testable without a Redis server or a running
    /// BackgroundService — see warptalk-ai/shared/schemas.py SuggestionResultMessage for the
    /// producing contract.
    ///
    /// An entry that claims type="suggestion" but cannot anchor to a bubble (no segment id)
    /// or has nothing to say (no content) is dropped rather than forwarded: the client has
    /// no way to render either case, and falling through to the legacy branch would put it
    /// in the summary panel instead.
    /// </summary>
    /// <summary>
    /// Reads the STT model's confidence off an stt:results entry, or <c>null</c> when the producer
    /// reported none.
    /// </summary>
    /// <remarks>
    /// WT-277: this used to be <c>float.TryParse(...) ? conf : 1.0f</c>, so a segment with no
    /// confidence at all was pushed to every client as maximum confidence. The rules (absent /
    /// unparsable / warptalk-ai's -1.0 "no logprobs" sentinel ⇒ unknown) live in
    /// <see cref="WarpTalk.Shared.ModelConfidence"/> so this and TranscriptService's persistence
    /// consumer cannot drift apart — the live caption and the stored row must agree.
    /// </remarks>
    public static float? TryReadSttConfidence(StreamEntry entry) =>
        (float?)WarpTalk.Shared.ModelConfidence.Parse(RedisStreamService.GetField(entry, "confidence"));

    public static AiSuggestionDto? TryReadSuggestion(StreamEntry entry, string translationRoomId)
    {
        if (RedisStreamService.GetField(entry, "type") != "suggestion")
            return null;

        var segmentId = RedisStreamService.GetField(entry, "segment_id") ?? "";
        var content = RedisStreamService.GetField(entry, "content") ?? "";
        if (string.IsNullOrWhiteSpace(segmentId) || string.IsNullOrWhiteSpace(content))
            return null;

        var detail = RedisStreamService.GetField(entry, "detail");

        // InvariantCulture is required, not cosmetic: the producer always writes "0.82",
        // and a host whose current culture uses "," as the decimal separator parses that
        // as the integer 82 — a low-confidence hint would arrive looking maximally certain.
        var confidence = float.TryParse(
            RedisStreamService.GetField(entry, "confidence"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsedConfidence)
                ? parsedConfidence
                : 0f;

        return new AiSuggestionDto(
            TranslationRoomId: translationRoomId,
            SegmentId: segmentId,
            Category: RedisStreamService.GetField(entry, "category") ?? "",
            Content: content,
            Detail: string.IsNullOrWhiteSpace(detail) ? null : detail,
            Confidence: confidence,
            Language: RedisStreamService.GetField(entry, "language") ?? "",
            CreatedAt: DateTime.UtcNow,
            SourcesJson: NullIfBlank(RedisStreamService.GetField(entry, "sources_json")));
    }

    /// <summary>
    /// A hint that named no document publishes an empty field, which is "no sources" and not an
    /// empty array. Null is what the client checks for.
    /// </summary>
    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
