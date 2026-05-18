using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.StateMachines;

namespace WarpTalk.TranslationRoomService.Application.BackgroundProcessors;

public class TelemetryProcessorService : ITelemetryProcessorService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TelemetryProcessorService> _logger;
    private readonly IAudioRouteEventProcessorService _eventProcessor;
    private readonly IOptionsMonitor<TelemetrySettings> _optionsMonitor;

    public TelemetryProcessorService(
        IConnectionMultiplexer redis, 
        ILogger<TelemetryProcessorService> logger,
        IAudioRouteEventProcessorService eventProcessor,
        IOptionsMonitor<TelemetrySettings> optionsMonitor)
    {
        _redis = redis;
        _logger = logger;
        _eventProcessor = eventProcessor;
        _optionsMonitor = optionsMonitor;
    }

    public async Task<Result> ProcessTelemetryAsync(TelemetryPayload payload, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var hashKey = RedisKeyHelper.GetTelemetryStateKey(payload.RoomId);

            // 1. Fetch current live-reloaded settings with self-healing Docker fallbacks
            var settings = _optionsMonitor.CurrentValue;
            var sttDegraded = settings.SttDegradedMs > 0 ? settings.SttDegradedMs : 3000.0;
            var sttRecovery = settings.SttRecoveryMs > 0 ? settings.SttRecoveryMs : 1500.0;
            var translationDegraded = settings.TranslationDegradedMs > 0 ? settings.TranslationDegradedMs : 2500.0;
            var translationRecovery = settings.TranslationRecoveryMs > 0 ? settings.TranslationRecoveryMs : 1200.0;
            var ttsDegraded = settings.TtsDegradedMs > 0 ? settings.TtsDegradedMs : 6000.0;
            var ttsRecovery = settings.TtsRecoveryMs > 0 ? settings.TtsRecoveryMs : 3000.0;
            var warmupThreshold = settings.WarmupCount > 0 ? settings.WarmupCount : 3;

            // 2. Fetch centralized telemetry state from Redis Hash
            var stateEntries = await db.HashGetAllAsync(hashKey);
            
            double oldSttEma = 0, oldTranslationEma = 0, oldTtsEma = 0;
            bool isSttDegraded = false, isTranslationDegraded = false, isTtsDegraded = false;
            int warmupCount = 0;
            long lastTimestamp = 0;

            foreach (var entry in stateEntries)
            {
                if (entry.Name == "stt_ema") oldSttEma = (double)entry.Value;
                else if (entry.Name == "translation_ema") oldTranslationEma = (double)entry.Value;
                else if (entry.Name == "tts_ema") oldTtsEma = (double)entry.Value;
                else if (entry.Name == "is_stt_degraded") isSttDegraded = (bool)entry.Value;
                else if (entry.Name == "is_translation_degraded") isTranslationDegraded = (bool)entry.Value;
                else if (entry.Name == "is_tts_degraded") isTtsDegraded = (bool)entry.Value;
                else if (entry.Name == "warmup_count") warmupCount = (int)entry.Value;
                else if (entry.Name == "last_timestamp") lastTimestamp = (long)entry.Value;
            }

            // 3. Warm-up sequence to prevent cold-start latency false alerts
            if (warmupCount < warmupThreshold)
            {
                warmupCount++;
                _logger.LogInformation("Room {RoomId} Telemetry warmup count: {Count}/{WarmupThreshold}. Latency: {LatencyMs}ms (Worker: {Worker})", 
                    payload.RoomId, warmupCount, warmupThreshold, payload.LatencyMs, payload.WorkerType);
                await db.HashSetAsync(hashKey, new HashEntry[] { 
                    new HashEntry("warmup_count", warmupCount),
                    new HashEntry("last_timestamp", payload.Timestamp)
                });
                return Result.Success();
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
            var updates = new List<HashEntry> { new HashEntry("last_timestamp", payload.Timestamp) };

            if (payload.WorkerType.Equals("stt", StringComparison.OrdinalIgnoreCase))
            {
                double newSttEma = oldSttEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldSttEma * (1 - alpha));
                updates.Add(new HashEntry("stt_ema", newSttEma));

                _logger.LogDebug("STT Ema calculated: {NewEma}ms (alpha: {Alpha}) for Room {RoomId}", newSttEma, alpha, payload.RoomId);

                if (!isSttDegraded && newSttEma > sttDegraded)
                {
                    isSttDegraded = true;
                    updates.Add(new HashEntry("is_stt_degraded", true));
                    _logger.LogWarning("STT Ema {Ema} exceeds threshold {Threshold}. Status updated in Redis.", newSttEma, sttDegraded);
                }
                else if (isSttDegraded && newSttEma < sttRecovery)
                {
                    isSttDegraded = false;
                    updates.Add(new HashEntry("is_stt_degraded", false));
                    _logger.LogInformation("STT Ema {Ema} recovered below threshold {Threshold}. Status updated in Redis.", newSttEma, sttRecovery);
                }
            }
            else if (payload.WorkerType.Equals("translation", StringComparison.OrdinalIgnoreCase))
            {
                double newTranslationEma = oldTranslationEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldTranslationEma * (1 - alpha));
                updates.Add(new HashEntry("translation_ema", newTranslationEma));

                _logger.LogDebug("Translation Ema calculated: {NewEma}ms (alpha: {Alpha}) for Room {RoomId}", newTranslationEma, alpha, payload.RoomId);

                if (!isTranslationDegraded && newTranslationEma > translationDegraded)
                {
                    isTranslationDegraded = true;
                    updates.Add(new HashEntry("is_translation_degraded", true));
                    _logger.LogWarning("Translation Ema {Ema} exceeds threshold {Threshold}. Status updated in Redis.", newTranslationEma, translationDegraded);
                }
                else if (isTranslationDegraded && newTranslationEma < translationRecovery)
                {
                    isTranslationDegraded = false;
                    updates.Add(new HashEntry("is_translation_degraded", false));
                    _logger.LogInformation("Translation Ema {Ema} recovered below threshold {Threshold}. Status updated in Redis.", newTranslationEma, translationRecovery);
                }
            }
            else if (payload.WorkerType.Equals("tts", StringComparison.OrdinalIgnoreCase))
            {
                double newTtsEma = oldTtsEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldTtsEma * (1 - alpha));
                updates.Add(new HashEntry("tts_ema", newTtsEma));

                _logger.LogDebug("TTS Ema calculated: {NewEma}ms (alpha: {Alpha}) for Room {RoomId}", newTtsEma, alpha, payload.RoomId);

                if (!isTtsDegraded && newTtsEma > ttsDegraded)
                {
                    isTtsDegraded = true;
                    updates.Add(new HashEntry("is_tts_degraded", true));
                    _logger.LogWarning("TTS Ema {Ema} exceeds threshold {Threshold}. Status updated in Redis.", newTtsEma, ttsDegraded);
                }
                else if (isTtsDegraded && newTtsEma < ttsRecovery)
                {
                    isTtsDegraded = false;
                    updates.Add(new HashEntry("is_tts_degraded", false));
                    _logger.LogInformation("TTS Ema {Ema} recovered below threshold {Threshold}. Status updated in Redis.", newTtsEma, ttsRecovery);
                }
            }

            // 6. Save updated telemetry metrics back to Redis Hash
            await db.HashSetAsync(hashKey, updates.ToArray());
            
            // Set 24 hour TTL on telemetry key for automated lifecycle cleanup
            await db.KeyExpireAsync(hashKey, TimeSpan.FromHours(24));

            // 7. Resolve Effective Canonical Status using Priority Resolver
            var voiceCloneStatusVal = await db.HashGetAsync(hashKey, "voice_clone_status");
            var deliveryModeVal = await db.HashGetAsync(hashKey, "delivery_mode");

            string voiceCloneStatus = voiceCloneStatusVal.HasValue ? voiceCloneStatusVal.ToString() : "NORMAL";
            string deliveryMode = deliveryModeVal.HasValue ? deliveryModeVal.ToString() : "NORMAL";

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
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing telemetry for room {RoomId}", payload.RoomId);
            return Result.Failure(AudioRouteConstants.ErrorFailedToProcessTelemetry, ErrorCodes.InternalServerError);
        }
    }
}
