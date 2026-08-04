using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Background service that consumes AI pipeline results from Redis Streams
/// and pushes them to connected clients via SignalR.
///
/// Streams consumed per active translationRoom:
///   - stt:results:{translationRoomId}     → TranscriptSegmentReceived (original transcript)
///   - tts:results:{translationRoomId}     → TranslatedAudioReceived (translated + cloned voice) 
///   - ai_assistant:results:{translationRoomId} → AiAssistantResult (summaries, action items)
///                                              → AiSuggestionReceived when type="suggestion"
///
/// Design: AI Assistant runs on its own consumer group on stt:results,
/// completely isolated from the Translation → TTS pipeline.
/// </summary>
public sealed class AiResultConsumerService : BackgroundService
{
    private readonly RedisStreamService _streamService;
    private readonly ActiveTranslationRoomRegistry _translationRoomRegistry;
    private readonly IHubContext<TranslationRoomHub> _hubContext;
    private readonly WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient _roomClient;
    private readonly ILogger<AiResultConsumerService> _logger;

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

    public AiResultConsumerService(
        RedisStreamService streamService,
        ActiveTranslationRoomRegistry translationRoomRegistry,
        IHubContext<TranslationRoomHub> hubContext,
        WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient workspaceClient,
        WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient roomClient,
        ILogger<AiResultConsumerService> logger)
    {
        _streamService = streamService;
        _translationRoomRegistry = translationRoomRegistry;
        _hubContext = hubContext;
        _workspaceClient = workspaceClient;
        _roomClient = roomClient;
        _logger = logger;
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
                ConsumeAiAssistantResultsAsync(stoppingToken));
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

    // ── STT Results → TranscriptSegmentReceived ──────────────

    private async Task ConsumeSTTResultsAsync(CancellationToken ct)
    {
        var streamKey = "stt:results";

        await _streamService.EnsureConsumerGroupAsync(streamKey, ConsumerGroupName);

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
                    if (string.IsNullOrEmpty(translationRoomId)) continue;

                    var originalText = RedisStreamService.GetField(entry, "text") ?? "";

                    if (await IsProfanityFilterEnabledAsync(translationRoomId, ct))
                    {
                        originalText = WarpTalk.Gateway.Helpers.ProfanityFilterHelper.MaskProfanity(originalText);
                    }

                    var segment = new TranscriptSegmentDto(
                        SegmentId: Guid.TryParse(RedisStreamService.GetField(entry, "segment_id"), out var sid) ? sid : Guid.NewGuid(),
                        SpeakerId: Guid.TryParse(RedisStreamService.GetField(entry, "speaker_id"), out var spk) ? spk : Guid.Empty,
                        SpeakerName: RedisStreamService.GetField(entry, "speaker_id") ?? "Unknown",
                        OriginalText: originalText,
                        OriginalLanguage: RedisStreamService.GetField(entry, "language") ?? "unknown",
                        TranslatedText: null,
                        TargetLanguage: null,
                        Confidence: float.TryParse(RedisStreamService.GetField(entry, "confidence"), out var conf) ? conf : 1.0f,
                        StartTimeMs: int.TryParse(RedisStreamService.GetField(entry, "start_ms"), out var start) ? start : 0,
                        EndTimeMs: int.TryParse(RedisStreamService.GetField(entry, "end_ms"), out var end) ? end : 0);

                    await _hubContext.Clients
                        .Group($"translationRoom:{translationRoomId}")
                        .SendAsync("TranscriptSegmentReceived", segment, ct);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming STT results");
                await Task.Delay(1000, ct);
            }
        }
    }

    // ── Translation Results → TranslationTextReceived ────────

    private async Task ConsumeTranslationResultsAsync(CancellationToken ct)
    {
        var streamKey = "translate:results";

        await _streamService.EnsureConsumerGroupAsync(streamKey, ConsumerGroupName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ConsumeAsync(
                    streamKey, ConsumerGroupName, _consumerName, count: 10);

                foreach (var entry in entries)
                {
                    var translationRoomId = RedisStreamService.GetField(entry, "meeting_id") ?? "";
                    if (string.IsNullOrEmpty(translationRoomId)) continue;

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

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming Translation results");
                await Task.Delay(2000, ct);
            }
        }
    }

    // ── TTS Results → TranslatedAudioReceived ────────────────

    private async Task ConsumeTTSResultsAsync(CancellationToken ct)
    {
        var streamKey = "tts:results";

        await _streamService.EnsureConsumerGroupAsync(streamKey, ConsumerGroupName);

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
                    if (string.IsNullOrEmpty(translationRoomId)) continue;
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
                _logger.LogError(ex, "Error consuming TTS results");
                await Task.Delay(1000, ct);
            }
        }
    }

    // ── AI Assistant Results → AiAssistantResult ─────────────

    private async Task ConsumeAiAssistantResultsAsync(CancellationToken ct)
    {
        var streamKey = "ai_assistant:results";

        await _streamService.EnsureConsumerGroupAsync(streamKey, ConsumerGroupName);

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
                    if (string.IsNullOrEmpty(translationRoomId)) continue;

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
            CreatedAt: DateTime.UtcNow);
    }
}
