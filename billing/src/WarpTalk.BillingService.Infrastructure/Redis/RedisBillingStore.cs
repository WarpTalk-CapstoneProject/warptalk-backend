using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Infrastructure.Redis;

public class RedisBillingStore : IRedisBillingStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    private const string ReservationZSetKey = "warptalk:billing:reservations_zset";
    private const string ReservationHashKey = "warptalk:billing:reservations_hash";

    private const string SessionZSetKey = "warptalk:billing:sessions_zset";
    private const string TempUsageLogDtoListKey = "warptalk:billing:temp_usage_logs";

    public RedisBillingStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _db = _redis.GetDatabase();
    }

    public async Task SetReservationAsync(RedisCreditReservationDto reservation, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var expireTime = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(reservation);

        var t1 = _db.SortedSetAddAsync(ReservationZSetKey, reservation.IdempotencyKey, expireTime);
        var t2 = _db.HashSetAsync(ReservationHashKey, reservation.IdempotencyKey, json);
        await Task.WhenAll(t1, t2);
    }

    public async Task<RedisCreditReservationDto?> GetAndRemoveReservationAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var json = await _db.HashGetAsync(ReservationHashKey, idempotencyKey);
        if (json.IsNullOrEmpty) return null;

        var t1 = _db.SortedSetRemoveAsync(ReservationZSetKey, idempotencyKey);
        var t2 = _db.HashDeleteAsync(ReservationHashKey, idempotencyKey);
        await Task.WhenAll(t1, t2);

        return JsonSerializer.Deserialize<RedisCreditReservationDto>((string)json!);
    }    public async Task<IEnumerable<RedisCreditReservationDto>> GetExpiredReservationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var maxScore = now.ToUnixTimeMilliseconds();
        var expiredKeys = await _db.SortedSetRangeByScoreAsync(ReservationZSetKey, 0, maxScore);

        var reservations = new List<RedisCreditReservationDto>();
        foreach (var key in expiredKeys)
        {
            var json = await _db.HashGetAsync(ReservationHashKey, key);
            if (!json.IsNullOrEmpty)
            {
                var res = JsonSerializer.Deserialize<RedisCreditReservationDto>((string)json!);
                if (res != null) reservations.Add(res);
            }
        }
        return reservations;
    }

    public async Task RemoveReservationAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var t1 = _db.SortedSetRemoveAsync(ReservationZSetKey, idempotencyKey);
        var t2 = _db.HashDeleteAsync(ReservationHashKey, idempotencyKey);
        await Task.WhenAll(t1, t2);
    }

    public async Task SetSessionActiveAsync(Guid sessionId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var expireTime = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds();
        await _db.SortedSetAddAsync(SessionZSetKey, sessionId.ToString(), expireTime);
    }

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var score = await _db.SortedSetScoreAsync(SessionZSetKey, sessionId.ToString());
        if (!score.HasValue) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return score.Value > now;
    }

    public async Task<IEnumerable<Guid>> GetExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var maxScore = now.ToUnixTimeMilliseconds();
        var expiredKeys = await _db.SortedSetRangeByScoreAsync(SessionZSetKey, 0, maxScore);

        var sessions = new List<Guid>();
        foreach (var key in expiredKeys)
        {
            if (Guid.TryParse(key.ToString(), out var id))
            {
                sessions.Add(id);
            }
        }
        return sessions;
    }

    public async Task RemoveSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _db.SortedSetRemoveAsync(SessionZSetKey, sessionId.ToString());
    }

    public async Task PushTempUsageLogDtoAsync(TempUsageLogDto log, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(log);
        await _db.ListRightPushAsync(TempUsageLogDtoListKey, json);
    }

    public async Task<IEnumerable<TempUsageLogDto>> GetAndClearTempUsageLogDtosAsync(CancellationToken cancellationToken = default)
    {
        var transaction = _db.CreateTransaction();
        var listLengthTask = transaction.ListLengthAsync(TempUsageLogDtoListKey);
        var popTask = transaction.ListLeftPopAsync(TempUsageLogDtoListKey, int.MaxValue); // Pop all available elements
        bool success = await transaction.ExecuteAsync();

        if (!success || popTask.Result == null || popTask.Result.Length == 0)
        {
            return Array.Empty<TempUsageLogDto>();
        }

        var logs = new List<TempUsageLogDto>();
        foreach (var item in popTask.Result)
        {
            if (!item.IsNullOrEmpty)
            {
                var log = JsonSerializer.Deserialize<TempUsageLogDto>((string)item!);
                if (log != null)
                {
                    logs.Add(log);
                }
            }
        }
        return logs;
    }
}
