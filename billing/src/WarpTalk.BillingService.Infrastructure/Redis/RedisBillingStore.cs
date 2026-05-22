using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Redis;

public class RedisBillingStore : IRedisBillingStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    
    private const string ReservationZSetKey = "warptalk:billing:reservations_zset";
    private const string ReservationHashKey = "warptalk:billing:reservations_hash";
    
    private const string SessionZSetKey = "warptalk:billing:sessions_zset";

    public RedisBillingStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _db = _redis.GetDatabase();
    }

    public async Task SetReservationAsync(RedisCreditReservation reservation, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var expireTime = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(reservation);
        
        var t1 = _db.SortedSetAddAsync(ReservationZSetKey, reservation.IdempotencyKey, expireTime);
        var t2 = _db.HashSetAsync(ReservationHashKey, reservation.IdempotencyKey, json);
        await Task.WhenAll(t1, t2);
    }

    public async Task<RedisCreditReservation?> GetAndRemoveReservationAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var json = await _db.HashGetAsync(ReservationHashKey, idempotencyKey);
        if (json.IsNullOrEmpty) return null;
        
        var t1 = _db.SortedSetRemoveAsync(ReservationZSetKey, idempotencyKey);
        var t2 = _db.HashDeleteAsync(ReservationHashKey, idempotencyKey);
        await Task.WhenAll(t1, t2);

        return JsonSerializer.Deserialize<RedisCreditReservation>((string)json!);
    }

    public async Task<IEnumerable<RedisCreditReservation>> GetAndRemoveExpiredReservationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var maxScore = now.ToUnixTimeMilliseconds();
        var expiredKeys = await _db.SortedSetRangeByScoreAsync(ReservationZSetKey, 0, maxScore);
        
        if (expiredKeys.Length == 0) return Array.Empty<RedisCreditReservation>();

        var reservations = new List<RedisCreditReservation>();
        var tasks = new List<Task>();
        foreach (var key in expiredKeys)
        {
            var json = await _db.HashGetAsync(ReservationHashKey, key);
            if (!json.IsNullOrEmpty)
            {
                var res = JsonSerializer.Deserialize<RedisCreditReservation>((string)json!);
                if (res != null) reservations.Add(res);
            }
            tasks.Add(_db.SortedSetRemoveAsync(ReservationZSetKey, key));
            tasks.Add(_db.HashDeleteAsync(ReservationHashKey, key));
        }
        
        await Task.WhenAll(tasks);
        return reservations;
    }

    public async Task SetSessionActiveAsync(Guid sessionId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var expireTime = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds();
        await _db.SortedSetAddAsync(SessionZSetKey, sessionId.ToString(), expireTime);
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
}
