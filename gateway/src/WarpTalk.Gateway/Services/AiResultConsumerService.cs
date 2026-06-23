using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
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

    // Cache profanity settings: translationRoomId -> isProfanityFilterEnabled
    private readonly ConcurrentDictionary<string, bool> _profanityFilterCache = new();

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

        // Start 4 parallel consumer loops for ALL translation rooms via unified streams
        _ = Task.Run(() => ConsumeSTTResultsAsync(stoppingToken));
        _ = Task.Run(() => ConsumeTranslationResultsAsync(stoppingToken));
        _ = Task.Run(() => ConsumeTTSResultsAsync(stoppingToken));
        _ = Task.Run(() => ConsumeAiAssistantResultsAsync(stoppingToken));

        // Keep running until shutdown
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
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
    {
        if (_profanityFilterCache.TryGetValue(translationRoomId, out var isEnabled))
            return isEnabled;

        try
        {
            var roomResponse = await _roomClient.GetTranslationRoomByIdAsync(
                new WarpTalk.Shared.Protos.GetTranslationRoomRequest { Id = translationRoomId }, cancellationToken: ct);
            
            var workspaceResponse = await _workspaceClient.GetWorkspaceSettingsAsync(
                new WarpTalk.Shared.Protos.GetWorkspaceSettingsRequest { WorkspaceId = roomResponse.WorkspaceId }, cancellationToken: ct);
            
            isEnabled = workspaceResponse.IsProfanityFilterEnabled;
            _profanityFilterCache.TryAdd(translationRoomId, isEnabled);
            return isEnabled;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            _profanityFilterCache.TryAdd(translationRoomId, false);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get profanity filter setting for room {RoomId}", translationRoomId);
            return false;
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
                        TargetLang: RedisStreamService.GetField(entry, "target_lang") ?? "");

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
}
