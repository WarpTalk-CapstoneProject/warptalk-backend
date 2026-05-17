using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.BackgroundProcessors;

public class TelemetryProcessorService : ITelemetryProcessorService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TelemetryProcessorService> _logger;
    
    // Ngưỡng cho STT
    private const double SttDegradedMs = 3000.0;
    private const double SttRecoveryMs = 1500.0;
    
    // Ngưỡng cho TTS
    private const double TtsDegradedMs = 6000.0;
    private const double TtsRecoveryMs = 3000.0;

    public TelemetryProcessorService(
        IConnectionMultiplexer redis, 
        ILogger<TelemetryProcessorService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<Result> ProcessTelemetryAsync(TelemetryPayload payload, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var hashKey = RedisKeyHelper.GetTelemetryStateKey(payload.RoomId);

            // 1. Lấy trạng thái Telemetry tập trung từ Redis Hash (Tránh Split-brain khi scale ngang)
            var stateEntries = await db.HashGetAllAsync(hashKey);
            
            double oldSttEma = 0, oldTtsEma = 0;
            bool isSttDegraded = false, isTtsDegraded = false;
            int warmupCount = 0;
            long lastTimestamp = 0;

            foreach (var entry in stateEntries)
            {
                if (entry.Name == "stt_ema") oldSttEma = (double)entry.Value;
                else if (entry.Name == "tts_ema") oldTtsEma = (double)entry.Value;
                else if (entry.Name == "is_stt_degraded") isSttDegraded = (bool)entry.Value;
                else if (entry.Name == "is_tts_degraded") isTtsDegraded = (bool)entry.Value;
                else if (entry.Name == "warmup_count") warmupCount = (int)entry.Value;
                else if (entry.Name == "last_timestamp") lastTimestamp = (long)entry.Value;
            }

            // 2. Kiểm tra Warm-up (Bỏ qua 3 chunk đầu khi mới khởi động phòng để tránh alert giả)
            if (warmupCount < 3)
            {
                warmupCount++;
                _logger.LogInformation("Room {RoomId} Telemetry warmup count: {Count}/3. Latency: {LatencyMs}ms", payload.RoomId, warmupCount, payload.LatencyMs);
                await db.HashSetAsync(hashKey, new HashEntry[] { 
                    new HashEntry("warmup_count", warmupCount),
                    new HashEntry("last_timestamp", payload.Timestamp)
                });
                return Result.Success();
            }

            // 3. Tính toán Hệ số mượt EMA thích ứng (Adaptive Alpha)
            double alpha = 0.3; // Mặc định
            if (lastTimestamp > 0)
            {
                double deltaSec = (payload.Timestamp - lastTimestamp) / 1000.0;
                if (deltaSec > 0)
                {
                    if (deltaSec < 1.0)
                    {
                        alpha = 0.1 + (0.2 * deltaSec); // Lọc mạnh khi mật độ nói dồn dập
                    }
                    else if (deltaSec > 3.0)
                    {
                        alpha = Math.Min(0.6, 0.3 + (0.1 * (deltaSec - 3.0))); // Phản hồi nhanh sau quãng lặng
                    }
                }
            }

            // 4. Áp dụng công thức tính toán EMA và đánh giá Hysteresis
            var updates = new List<HashEntry> { new HashEntry("last_timestamp", payload.Timestamp) };

            if (payload.WorkerType.Equals("stt", StringComparison.OrdinalIgnoreCase))
            {
                double newSttEma = oldSttEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldSttEma * (1 - alpha));
                updates.Add(new HashEntry("stt_ema", newSttEma));

                _logger.LogDebug("STT Ema calculated: {NewEma}ms (alpha: {Alpha}) for Room {RoomId}", newSttEma, alpha, payload.RoomId);

                if (!isSttDegraded && newSttEma > SttDegradedMs)
                {
                    isSttDegraded = true;
                    updates.Add(new HashEntry("is_stt_degraded", true));
                    _logger.LogWarning("STT Ema {Ema} exceeds threshold {Threshold}. Status updated in Redis.", newSttEma, SttDegradedMs);
                }
                else if (isSttDegraded && newSttEma < SttRecoveryMs)
                {
                    isSttDegraded = false;
                    updates.Add(new HashEntry("is_stt_degraded", false));
                    _logger.LogInformation("STT Ema {Ema} recovered below threshold {Threshold}. Status updated in Redis.", newSttEma, SttRecoveryMs);
                }
            }
            else if (payload.WorkerType.Equals("tts", StringComparison.OrdinalIgnoreCase))
            {
                double newTtsEma = oldTtsEma == 0 ? payload.LatencyMs : (payload.LatencyMs * alpha) + (oldTtsEma * (1 - alpha));
                updates.Add(new HashEntry("tts_ema", newTtsEma));

                _logger.LogDebug("TTS Ema calculated: {NewEma}ms (alpha: {Alpha}) for Room {RoomId}", newTtsEma, alpha, payload.RoomId);

                if (!isTtsDegraded && newTtsEma > TtsDegradedMs)
                {
                    isTtsDegraded = true;
                    updates.Add(new HashEntry("is_tts_degraded", true));
                    _logger.LogWarning("TTS Ema {Ema} exceeds threshold {Threshold}. Status updated in Redis.", newTtsEma, TtsDegradedMs);
                }
                else if (isTtsDegraded && newTtsEma < TtsRecoveryMs)
                {
                    isTtsDegraded = false;
                    updates.Add(new HashEntry("is_tts_degraded", false));
                    _logger.LogInformation("TTS Ema {Ema} recovered below threshold {Threshold}. Status updated in Redis.", newTtsEma, TtsRecoveryMs);
                }
            }

            // 5. Lưu trạng thái mới về Redis Hash
            await db.HashSetAsync(hashKey, updates.ToArray());
            
            // Set 24 hour TTL on telemetry key for cleanup
            await db.KeyExpireAsync(hashKey, TimeSpan.FromHours(24));
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing telemetry for room {RoomId}", payload.RoomId);
            return Result.Failure("Failed to process telemetry", ErrorCodes.InternalServerError);
        }
    }
}
