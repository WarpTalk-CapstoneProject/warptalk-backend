using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Presence;
using WarpTalk.Shared;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The Gateway half of <see cref="RoomHubLiveness"/>: one beat asserts every room this replica holds
/// a socket on, with an expiry, and keeps the heartbeat marker at the start of its unbroken run.
/// The room service's reapers read both; their half is pinned in AbandonedRoomSweepWorkerTests and
/// IdleRoomMonitoringWorkerTests.
/// </summary>
public sealed class RoomLivenessHeartbeatServiceTests
{
    [Fact]
    public async Task ABeat_AssertsEveryRoomThisReplicaHoldsASocketOn_WithAnExpiry()
    {
        var redis = new RecordingRedis();
        var roomA = Guid.NewGuid();
        var roomB = Guid.NewGuid();

        await RoomLivenessHeartbeatService.BeatAsync(
            redis.Database, new[] { roomA.ToString(), roomB.ToString() }, DateTimeOffset.UtcNow);

        Assert.True(redis.Has(RoomHubLiveness.RoomKey(roomA)));
        Assert.True(redis.Has(RoomHubLiveness.RoomKey(roomB)));

        // Without an expiry a room key outlives the socket forever — the very failure this exists
        // to replace — so every one must carry the shared TTL.
        Assert.Equal(RoomHubLiveness.Ttl, redis.ExpiryOf(RoomHubLiveness.RoomKey(roomA)));
        Assert.Equal(RoomHubLiveness.Ttl, redis.ExpiryOf(RoomHubLiveness.RoomKey(roomB)));
    }

    [Fact]
    public async Task ABeat_KeepsTheMarkerAtTheStartOfTheRun_AndKeepsItAlive()
    {
        var redis = new RecordingRedis();
        var first = DateTimeOffset.UtcNow.AddMinutes(-15);

        await RoomLivenessHeartbeatService.BeatAsync(redis.Database, Array.Empty<string>(), first);
        await RoomLivenessHeartbeatService.BeatAsync(redis.Database, Array.Empty<string>(), DateTimeOffset.UtcNow);

        // The reader's warm-up is measured from this value. A beat that overwrote it would restart
        // the warm-up every 30 seconds and the reapers would never trust the heartbeat at all.
        Assert.Equal(first.ToUnixTimeMilliseconds().ToString(), redis.ValueOf(RoomHubLiveness.HeartbeatKey));
        Assert.Equal(RoomHubLiveness.Ttl, redis.ExpiryOf(RoomHubLiveness.HeartbeatKey));
    }

    [Fact]
    public async Task ABeatWithNoRooms_StillProvesTheHeartbeatIsRunning()
    {
        // "No room has a socket" is only believable from a Gateway that is demonstrably beating.
        var redis = new RecordingRedis();

        await RoomLivenessHeartbeatService.BeatAsync(redis.Database, Array.Empty<string>(), DateTimeOffset.UtcNow);

        Assert.True(redis.Has(RoomHubLiveness.HeartbeatKey));
    }

    [Fact]
    public void RoomKey_IsTheSameWhicheverWayTheRoomIdIsSpelled()
    {
        // The hub keys rooms by the string the client sent; the room service by its Guid.
        var roomId = Guid.NewGuid();

        Assert.Equal(RoomHubLiveness.RoomKey(roomId), RoomHubLiveness.RoomKey(roomId.ToString().ToUpperInvariant()));
    }

    /// <summary>
    /// Real SET / SET NX / EXPIRE semantics over the two StringSetAsync overloads StackExchange.Redis
    /// 3.x binds these calls to, so the assertions are about what Redis would hold, not about which
    /// overload happened to be picked.
    /// </summary>
    private sealed class RecordingRedis
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TimeSpan?> _expiries = new(StringComparer.Ordinal);

        public IDatabase Database { get; }

        public RecordingRedis()
        {
            var db = new Mock<IDatabase>();

            db.Setup(d => d.StringSetAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(),
                    It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, RedisValue value, Expiration expiry, ValueCondition _, CommandFlags _) =>
                {
                    _values[key.ToString()!] = value.ToString();
                    _expiries[key.ToString()!] = expiry.Equals((Expiration)RoomHubLiveness.Ttl)
                        ? RoomHubLiveness.Ttl
                        : expiry.Equals(default(Expiration)) ? null : TimeSpan.Zero;
                    return true;
                });

            db.Setup(d => d.StringSetAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
                .ReturnsAsync((RedisKey key, RedisValue value, TimeSpan? expiry, When when) =>
                {
                    var name = key.ToString()!;
                    if (when == When.NotExists && _values.ContainsKey(name)) return false;
                    _values[name] = value.ToString();
                    _expiries[name] = expiry;
                    return true;
                });

            db.Setup(d => d.KeyExpireAsync(
                    It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, TimeSpan? expiry, ExpireWhen _, CommandFlags _) =>
                {
                    var name = key.ToString()!;
                    if (!_values.ContainsKey(name)) return false;
                    _expiries[name] = expiry;
                    return true;
                });

            Database = db.Object;
        }

        public bool Has(string key) => _values.ContainsKey(key);

        public string? ValueOf(string key) => _values.GetValueOrDefault(key);

        public TimeSpan? ExpiryOf(string key) => _expiries.GetValueOrDefault(key);
    }
}
