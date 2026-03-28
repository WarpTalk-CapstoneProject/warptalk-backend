using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using WarpTalk.Gateway.Hubs;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Background service that consumes AI pipeline results from Redis Streams
/// and pushes them to connected clients via SignalR.
///
/// Streams consumed per active meeting:
///   - stt:results:{meetingId}     → TranscriptSegmentReceived (original transcript)
///   - tts:results:{meetingId}     → TranslatedAudioReceived (translated + cloned voice) 
///   - ai_assistant:results:{meetingId} → AiAssistantResult (summaries, action items)
///
/// Design: AI Assistant runs on its own consumer group on stt:results,
/// completely isolated from the Translation → TTS pipeline.
/// </summary>
public sealed class AiResultConsumerService : BackgroundService
{
    private readonly RedisStreamService _streamService;
    private readonly ActiveMeetingRegistry _meetingRegistry;
    private readonly IHubContext<MeetingHub> _hubContext;
    private readonly ILogger<AiResultConsumerService> _logger;

    private const string ConsumerGroupName = "gateway-consumers";
    private readonly string _consumerName = $"gateway-{Environment.MachineName}-{Guid.NewGuid():N[..8]}";

    // meetingId → CancellationTokenSource (for stopping consumers when meeting ends)
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _meetingCts = new();

    public AiResultConsumerService(
        RedisStreamService streamService,
        ActiveMeetingRegistry meetingRegistry,
        IHubContext<MeetingHub> hubContext,
        ILogger<AiResultConsumerService> logger)
    {
        _streamService = streamService;
        _meetingRegistry = meetingRegistry;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AiResultConsumerService starting, consumer={Consumer}", _consumerName);

        // Subscribe to meeting lifecycle events
        _meetingRegistry.MeetingActivated += OnMeetingActivated;
        _meetingRegistry.MeetingDeactivated += OnMeetingDeactivated;

        // Start consuming for any meetings already active (e.g., after gateway restart)
        foreach (var meetingId in _meetingRegistry.GetActiveMeetingIds())
        {
            OnMeetingActivated(meetingId);
        }

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
            _meetingRegistry.MeetingActivated -= OnMeetingActivated;
            _meetingRegistry.MeetingDeactivated -= OnMeetingDeactivated;

            // Cancel all active consumers
            foreach (var (_, cts) in _meetingCts)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _meetingCts.Clear();

            _logger.LogInformation("AiResultConsumerService stopped");
        }
    }

    private void OnMeetingActivated(string meetingId)
    {
        if (_meetingCts.ContainsKey(meetingId))
            return;

        var cts = new CancellationTokenSource();
        if (!_meetingCts.TryAdd(meetingId, cts))
        {
            cts.Dispose();
            return;
        }

        _logger.LogInformation("Starting AI result consumers for meeting {MeetingId}", meetingId);

        // Start 4 parallel consumer loops for this meeting
        _ = Task.Run(() => ConsumeSTTResultsAsync(meetingId, cts.Token));
        _ = Task.Run(() => ConsumeTranslationResultsAsync(meetingId, cts.Token));
        _ = Task.Run(() => ConsumeTTSResultsAsync(meetingId, cts.Token));
        _ = Task.Run(() => ConsumeAiAssistantResultsAsync(meetingId, cts.Token));
    }

    private void OnMeetingDeactivated(string meetingId)
    {
        if (_meetingCts.TryRemove(meetingId, out var cts))
        {
            _logger.LogInformation("Stopping AI result consumers for meeting {MeetingId}", meetingId);
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ── STT Results → TranscriptSegmentReceived ──────────────

    private async Task ConsumeSTTResultsAsync(string meetingId, CancellationToken ct)
    {
        var streamKey = $"stt:results:{meetingId}";
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
                    var segment = new TranscriptSegmentDto(
                        SegmentId: Guid.TryParse(RedisStreamService.GetField(entry, "segment_id"), out var sid) ? sid : Guid.NewGuid(),
                        SpeakerId: Guid.TryParse(RedisStreamService.GetField(entry, "speaker_id"), out var spk) ? spk : Guid.Empty,
                        SpeakerName: RedisStreamService.GetField(entry, "speaker_id") ?? "Unknown",
                        OriginalText: RedisStreamService.GetField(entry, "text") ?? "",
                        OriginalLanguage: RedisStreamService.GetField(entry, "language") ?? "unknown",
                        TranslatedText: null,
                        TargetLanguage: null,
                        Confidence: float.TryParse(RedisStreamService.GetField(entry, "confidence"), out var conf) ? conf : 1.0f,
                        StartTimeMs: int.TryParse(RedisStreamService.GetField(entry, "start_ms"), out var start) ? start : 0,
                        EndTimeMs: int.TryParse(RedisStreamService.GetField(entry, "end_ms"), out var end) ? end : 0);

                    await _hubContext.Clients
                        .Group($"meeting:{meetingId}")
                        .SendAsync("TranscriptSegmentReceived", segment, ct);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming STT results for meeting {MeetingId}", meetingId);
                await Task.Delay(1000, ct);
            }
        }
    }

    // ── Translation Results → TranslationTextReceived ────────

    private async Task ConsumeTranslationResultsAsync(string meetingId, CancellationToken ct)
    {
        var streamKey = $"translate:results:{meetingId}";
        await _streamService.EnsureConsumerGroupAsync(streamKey, ConsumerGroupName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ConsumeAsync(
                    streamKey, ConsumerGroupName, _consumerName, count: 10);

                foreach (var entry in entries)
                {
                    var dto = new TranslationTextDto(
                        SegmentId: RedisStreamService.GetField(entry, "segment_id") ?? "",
                        SpeakerId: Guid.TryParse(RedisStreamService.GetField(entry, "speaker_id"), out var spk) ? spk : Guid.Empty,
                        OriginalText: RedisStreamService.GetField(entry, "original_text") ?? "",
                        TranslatedText: RedisStreamService.GetField(entry, "translated_text") ?? "",
                        SourceLang: RedisStreamService.GetField(entry, "source_lang") ?? "",
                        TargetLang: RedisStreamService.GetField(entry, "target_lang") ?? "");

                    await _hubContext.Clients
                        .Group($"meeting:{meetingId}")
                        .SendAsync("TranslationTextReceived", dto, ct);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming Translation results for meeting {MeetingId}", meetingId);
                await Task.Delay(2000, ct);
            }
        }
    }

    // ── TTS Results → TranslatedAudioReceived ────────────────

    private async Task ConsumeTTSResultsAsync(string meetingId, CancellationToken ct)
    {
        var streamKey = $"tts:results:{meetingId}";
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
                    var audioDto = new TranslatedAudioDto(
                        SegmentId: RedisStreamService.GetField(entry, "segment_id") ?? "",
                        SpeakerId: Guid.TryParse(RedisStreamService.GetField(entry, "speaker_id"), out var spk) ? spk : Guid.Empty,
                        AudioBase64: RedisStreamService.GetField(entry, "audio_data") ?? "",
                        VoiceType: RedisStreamService.GetField(entry, "voice_type") ?? "default",
                        DurationMs: int.TryParse(RedisStreamService.GetField(entry, "duration_ms"), out var dur) ? dur : 0);

                    await _hubContext.Clients
                        .Group($"meeting:{meetingId}")
                        .SendAsync("TranslatedAudioReceived", audioDto, ct);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());

                    _logger.LogDebug(
                        "Delivered TTS audio: meeting={MeetingId}, segment={SegmentId}, voice={VoiceType}",
                        meetingId, audioDto.SegmentId, audioDto.VoiceType);
                }

                if (entries.Length == 0)
                    await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming TTS results for meeting {MeetingId}", meetingId);
                await Task.Delay(1000, ct);
            }
        }
    }

    // ── AI Assistant Results → AiAssistantResult ─────────────

    private async Task ConsumeAiAssistantResultsAsync(string meetingId, CancellationToken ct)
    {
        var streamKey = $"ai_assistant:results:{meetingId}";
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
                    var result = new AiAssistantResultDto(
                        MeetingId: meetingId,
                        Type: RedisStreamService.GetField(entry, "type") ?? "summary",
                        Content: RedisStreamService.GetField(entry, "content") ?? "",
                        CreatedAt: DateTime.UtcNow);

                    await _hubContext.Clients
                        .Group($"meeting:{meetingId}")
                        .SendAsync("AiAssistantResult", result, ct);

                    await _streamService.AcknowledgeAsync(streamKey, ConsumerGroupName, entry.Id.ToString());
                }

                if (entries.Length == 0)
                    await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming AI Assistant results for meeting {MeetingId}", meetingId);
                await Task.Delay(2000, ct);
            }
        }
    }
}
