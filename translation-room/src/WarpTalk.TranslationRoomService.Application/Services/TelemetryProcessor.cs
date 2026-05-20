using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.StateMachines;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TelemetryProcessor : ITelemetryProcessor
{
    private readonly IRedisStateRepository _redisStateRepo;
    private readonly ILogger<TelemetryProcessor> _logger;
    private readonly IAudioRouteEventProcessor _eventProcessor;
    private readonly IOptionsMonitor<TelemetrySettings> _optionsMonitor;

    public TelemetryProcessor(
        IRedisStateRepository redisStateRepo, 
        ILogger<TelemetryProcessor> logger,
        IAudioRouteEventProcessor eventProcessor,
        IOptionsMonitor<TelemetrySettings> optionsMonitor)
    {
        _redisStateRepo = redisStateRepo;
        _logger = logger;
        _eventProcessor = eventProcessor;
        _optionsMonitor = optionsMonitor;
    }

    public async Task ProcessTelemetryAsync(TelemetryPayload payload, CancellationToken ct = default)
    {
        try
        {
            var hashKey = CacheKeyHelper.GetTelemetryStateKey(payload.RoomId);

            // 1. Fetch current live-reloaded settings with self-healing Docker fallbacks
            var settings = _optionsMonitor.CurrentValue;
            var sttDegraded = settings.SttDegradedMs > 0 ? settings.SttDegradedMs : 3000.0;
            var sttRecovery = settings.SttRecoveryMs > 0 ? settings.SttRecoveryMs : 1500.0;
            var translationDegraded = settings.TranslationDegradedMs > 0 ? settings.TranslationDegradedMs : 2500.0;
            var translationRecovery = settings.TranslationRecoveryMs > 0 ? settings.TranslationRecoveryMs : 1200.0;
            var ttsDegraded = settings.TtsDegradedMs > 0 ? settings.TtsDegradedMs : 6000.0;
            var ttsRecovery = settings.TtsRecoveryMs > 0 ? settings.TtsRecoveryMs : 3000.0;
            var warmupThreshold = settings.WarmupCount > 0 ? settings.WarmupCount : 3;

            // 2. Fetch centralized telemetry state from Redis Hash via Repository
            var stateEntries = await _redisStateRepo.GetHashAllAsync(hashKey);
            
            double oldSttEma = 0, oldTranslationEma = 0, oldTtsEma = 0;
            bool isSttDegraded = false, isTranslationDegraded = false, isTtsDegraded = false;
            int warmupCount = 0;
            long lastTimestamp = 0;

            if (stateEntries.TryGetValue("stt_ema", out var sttEmaStr) && double.TryParse(sttEmaStr, out var sttEmaVal)) oldSttEma = sttEmaVal;
            if (stateEntries.TryGetValue("translation_ema", out var transEmaStr) && double.TryParse(transEmaStr, out var transEmaVal)) oldTranslationEma = transEmaVal;
            if (stateEntries.TryGetValue("tts_ema", out var ttsEmaStr) && double.TryParse(ttsEmaStr, out var ttsEmaVal)) oldTtsEma = ttsEmaVal;
            if (stateEntries.TryGetValue("is_stt_degraded", out var sttDegradedStr) && bool.TryParse(sttDegradedStr, out var sttDegradedVal)) isSttDegraded = sttDegradedVal;
            if (stateEntries.TryGetValue("is_translation_degraded", out var transDegradedStr) && bool.TryParse(transDegradedStr, out var transDegradedVal)) isTranslationDegraded = transDegradedVal;
            if (stateEntries.TryGetValue("is_tts_degraded", out var ttsDegradedStr) && bool.TryParse(ttsDegradedStr, out var ttsDegradedVal)) isTtsDegraded = ttsDegradedVal;
            if (stateEntries.TryGetValue("warmup_count", out var warmupStr) && int.TryParse(warmupStr, out var warmupVal)) warmupCount = warmupVal;
            if (stateEntries.TryGetValue("last_timestamp", out var lastTsStr) && long.TryParse(lastTsStr, out var lastTsVal)) lastTimestamp = lastTsVal;

            // 3. Warm-up sequence to prevent cold-start latency false alerts
            if (warmupCount < warmupThreshold)
            {
                warmupCount++;
                _logger.LogInformation("Room {RoomId} Telemetry warmup count: {Count}/{WarmupThreshold}. Latency: {LatencyMs}ms (Worker: {Worker})", 
                    payload.RoomId, warmupCount, warmupThreshold, payload.LatencyMs, payload.WorkerType);

                var warmupUpdates = new Dictionary<string, string>
                {
                    { "warmup_count", warmupCount.ToString() },
                    { "last_timestamp", payload.Timestamp.ToString() }
                };
                await _redisStateRepo.HashSetAsync(hashKey, warmupUpdates);
                return;
            }

            // 4. Calculate Adaptive Alpha (Smoothing Factor)
            double alpha = 0.3; // Default
            if (lastTimestamp > 0)
            {
                double deltaSec = (payload.Timestamp - lastTimestamp) / 1000.0;
                if (deltaSec > 0)
                {
                    if (deltaSec < 1.0)
                    {
                        alpha = 0.1 + (0.2 * deltaSec); // Filter bursts
                    }
                    else if (deltaSec > 3.0)
                    {
                        alpha = Math.Min(0.6, 0.3 + (0.1 * (deltaSec - 3.0))); // Recover after silences
                    }
                }
            }

            // 5. Evaluate EMA and Hysteresis rules independently per pipeline stage
            var updates = new Dictionary<string, string>
            {
                { "last_timestamp", payload.Timestamp.ToString() }
            };

            if (payload.WorkerType.Equals("stt", StringComparison.OrdinalIgnoreCase))
            {
                double newSttEma = oldSttEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldSttEma * (1 - alpha));
                updates["stt_ema"] = newSttEma.ToString();

                _logger.LogDebug("STT Ema calculated: {NewEma}ms (alpha: {Alpha}) for Room {RoomId}", newSttEma, alpha, payload.RoomId);

                if (!isSttDegraded && newSttEma > sttDegraded)
                {
                    isSttDegraded = true;
                    updates["is_stt_degraded"] = "True";
                    _logger.LogWarning("STT Ema {Ema} exceeds threshold {Threshold}. Status updated in Redis.", newSttEma, sttDegraded);
                }
                else if (isSttDegraded && newSttEma < sttRecovery)
                {
                    isSttDegraded = false;
                    updates["is_stt_degraded"] = "False";
                    _logger.LogInformation("STT Ema {Ema} recovered below threshold {Threshold}. Status updated in Redis.", newSttEma, sttRecovery);
                }
            }
            else if (payload.WorkerType.Equals("translation", StringComparison.OrdinalIgnoreCase))
            {
                double newTranslationEma = oldTranslationEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldTranslationEma * (1 - alpha));
                updates["translation_ema"] = newTranslationEma.ToString();

                _logger.LogDebug("Translation Ema calculated: {NewEma}ms (alpha: {Alpha}) for Room {RoomId}", newTranslationEma, alpha, payload.RoomId);

                if (!isTranslationDegraded && newTranslationEma > translationDegraded)
                {
                    isTranslationDegraded = true;
                    updates["is_translation_degraded"] = "True";
                    _logger.LogWarning("Translation Ema {Ema} exceeds threshold {Threshold}. Status updated in Redis.", newTranslationEma, translationDegraded);
                }
                else if (isTranslationDegraded && newTranslationEma < translationRecovery)
                {
                    isTranslationDegraded = false;
                    updates["is_translation_degraded"] = "False";
                    _logger.LogInformation("Translation Ema {Ema} recovered below threshold {Threshold}. Status updated in Redis.", newTranslationEma, translationRecovery);
                }
            }
            else if (payload.WorkerType.Equals("tts", StringComparison.OrdinalIgnoreCase))
            {
                double newTtsEma = oldTtsEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldTtsEma * (1 - alpha));
                updates["tts_ema"] = newTtsEma.ToString();

                _logger.LogDebug("TTS Ema calculated: {NewEma}ms (alpha: {Alpha}) for Room {RoomId}", newTtsEma, alpha, payload.RoomId);

                if (!isTtsDegraded && newTtsEma > ttsDegraded)
                {
                    isTtsDegraded = true;
                    updates["is_tts_degraded"] = "True";
                    _logger.LogWarning("TTS Ema {Ema} exceeds threshold {Threshold}. Status updated in Redis.", newTtsEma, ttsDegraded);
                }
                else if (isTtsDegraded && newTtsEma < ttsRecovery)
                {
                    isTtsDegraded = false;
                    updates["is_tts_degraded"] = "False";
                    _logger.LogInformation("TTS Ema {Ema} recovered below threshold {Threshold}. Status updated in Redis.", newTtsEma, ttsRecovery);
                }
            }

            // 6. Save updated telemetry metrics back to Redis Hash
            await _redisStateRepo.HashSetAsync(hashKey, updates);
            
            // Set 24 hour TTL on telemetry key for automated lifecycle cleanup
            await _redisStateRepo.KeyExpireAsync(hashKey, TimeSpan.FromHours(24));

            // 7. Resolve Effective Canonical Status using Priority Resolver
            string voiceCloneStatus = await _redisStateRepo.HashGetAsync(hashKey, "voice_clone_status") ?? "NORMAL";
            string deliveryMode = await _redisStateRepo.HashGetAsync(hashKey, "delivery_mode") ?? "NORMAL";

            var resolvedStatus = AudioRoutePriorityResolver.ResolveEffectiveStatus(
                isSttDegraded,
                isTranslationDegraded,
                isTtsDegraded,
                voiceCloneStatus,
                deliveryMode);

            _logger.LogInformation("Resolved effective status {Status} for Room {RoomId} / Route {RouteId}", 
                resolvedStatus, payload.RoomId, payload.RouteId);

            // Synchronize the resolved canonical status to PostgreSQL
            var eventPayload = $"{{\"status\":\"{resolvedStatus}\"}}";
            await _eventProcessor.ProcessEventAsync(payload.RoomId, payload.RouteId, AudioRoutingEventType.telemetry_state_updated.ToString(), eventPayload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing telemetry for room {RoomId}", payload.RoomId);
            throw;
        }
    }
}
