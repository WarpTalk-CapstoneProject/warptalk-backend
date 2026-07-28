using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Infrastructure.Redis;

public class RedisBillingStore : IRedisBillingStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public RedisBillingStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _db = _redis.GetDatabase();
    }

    public async Task<Result> SetSessionActiveAsync(Guid sessionId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        try
        {
            var expireTime = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds();
            await _db.SortedSetAddAsync(RedisConstants.Keys.SessionZSet, sessionId.ToString(), expireTime);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<bool>> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var score = await _db.SortedSetScoreAsync(RedisConstants.Keys.SessionZSet, sessionId.ToString());
            if (!score.HasValue) return Result.Success(false);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Result.Success(score.Value > now);
        }
        catch (Exception ex)
        {
            return Result.Failure<bool>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<IEnumerable<Guid>>> GetExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        try
        {
            var maxScore = now.ToUnixTimeMilliseconds();
            var expiredKeys = await _db.SortedSetRangeByScoreAsync(RedisConstants.Keys.SessionZSet, 0, maxScore);

            var sessions = new List<Guid>();
            foreach (var key in expiredKeys)
            {
                if (Guid.TryParse(key.ToString(), out var id))
                    sessions.Add(id);
            }
            return Result.Success<IEnumerable<Guid>>(sessions);
        }
        catch (Exception ex)
        {
            return Result.Failure<IEnumerable<Guid>>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> RemoveSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.SortedSetRemoveAsync(RedisConstants.Keys.SessionZSet, sessionId.ToString());
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> PushTempUsageLogDtoAsync(TempUsageLogDto log, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(log);
            await _db.ListRightPushAsync(RedisConstants.Keys.TempUsageLogList, json);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<IReadOnlyList<TempUsageLogDto>>> GetTempUsageLogBatchAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await _db.ListRangeAsync(RedisConstants.Keys.TempUsageLogList, 0, batchSize - 1);

            if (items.Length == 0)
                return Result.Success<IReadOnlyList<TempUsageLogDto>>(Array.Empty<TempUsageLogDto>());

            var logs = new List<TempUsageLogDto>();
            foreach (var item in items)
            {
                if (!item.IsNullOrEmpty)
                {
                    var log = JsonSerializer.Deserialize<TempUsageLogDto>((string)item!);
                    if (log != null) logs.Add(log);
                }
            }
            return Result.Success<IReadOnlyList<TempUsageLogDto>>(logs);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<TempUsageLogDto>>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> TrimTempUsageLogBatchAsync(int processedCount, CancellationToken cancellationToken = default)
    {
        try
        {
            if (processedCount <= 0)
                return Result.Success();

            await _db.ListTrimAsync(RedisConstants.Keys.TempUsageLogList, processedCount, -1);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> SetAiServiceStateAsync(Guid workspaceId, string serviceState, string? suspendedReason, CancellationToken cancellationToken = default)
    {
        try
        {
            var key = string.Format(RedisConstants.Keys.WorkspaceAiServiceStateTemplate, workspaceId);
            var payload = JsonSerializer.Serialize(new
            {
                workspaceId,
                serviceState,
                suspendedReason,
                updatedAt = DateTime.UtcNow
            });

            await _db.StringSetAsync(key, payload, TimeSpan.FromHours(24));
            await _db.StringSetAsync(
                string.Format(RedisConstants.Keys.WorkspaceAiServiceSuspendedTemplate, workspaceId),
                serviceState == SubscriptionConstants.ServiceStates.Suspended ? RedisConstants.Values.True : RedisConstants.Values.False,
                TimeSpan.FromHours(24));

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> SetAiServiceStateForRoomAsync(Guid translationRoomId, string serviceState, string? suspendedReason, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                translationRoomId,
                serviceState,
                suspendedReason,
                updatedAt = DateTime.UtcNow
            });

            await _db.StringSetAsync(string.Format(RedisConstants.Keys.TranslationRoomAiServiceStateTemplate, translationRoomId), payload, TimeSpan.FromHours(24));
            await _db.StringSetAsync(
                string.Format(RedisConstants.Keys.TranslationRoomAiServiceSuspendedTemplate, translationRoomId),
                serviceState == SubscriptionConstants.ServiceStates.Suspended ? RedisConstants.Values.True : RedisConstants.Values.False,
                TimeSpan.FromHours(24));

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, ErrorCodes.InternalServerError);
        }
    }
}
