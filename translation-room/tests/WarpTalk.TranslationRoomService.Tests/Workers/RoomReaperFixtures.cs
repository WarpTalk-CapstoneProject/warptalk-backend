using System.Globalization;
using Moq;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// Rooms and rosters for the two abandoned-room reapers (IdleRoomMonitoringWorker and
/// AbandonedRoomSweepWorker), shaped the way production seeds them.
/// </summary>
internal static class RoomReaperFixtures
{
    public static readonly Guid HostId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static TranslationRoom Room(string type = TranslationRoomTypes.Event, string status = "IN_PROGRESS") => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        HostId = HostId,
        Title = "Weekly sync",
        TranslationRoomCode = "abc-defg-hij",
        Status = status,
        TranslationRoomType = type,
        SourceLanguage = "vi",
        TargetLanguages = "[\"en\"]",
        Settings = "{}",
        IsActive = true,
        StartedAt = DateTime.UtcNow.AddHours(-1),
        CreatedAt = DateTime.UtcNow.AddHours(-1),
    };

    /// <summary>
    /// The host, an hour into the meeting. JoinedAt is deliberately long past the idle timeout:
    /// that is exactly the row the old JoinedAt fallback read as "idle since they joined".
    /// </summary>
    public static TranslationRoomParticipant Host(TranslationRoom room, string status) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = room.Id,
        UserId = room.HostId,
        DisplayName = "Host",
        Role = "HOST",
        SpeakLanguage = "vi",
        ListenLanguage = "vi",
        Status = status,
        ConnectionType = "WEBRTC",
        JoinedAt = DateTime.UtcNow.AddHours(-1),
        LeftAt = status == TranslationRoomParticipantStatuses.Left ? DateTime.UtcNow.AddSeconds(-10) : null,
    };

    /// <summary>An ordinary participant who joined over REST, half an hour into the meeting.</summary>
    public static TranslationRoomParticipant Guest(TranslationRoom room, string status) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = room.Id,
        UserId = Guid.NewGuid(),
        DisplayName = "Guest",
        Role = "PARTICIPANT",
        SpeakLanguage = "en",
        ListenLanguage = "en",
        Status = status,
        ConnectionType = "webrtc",
        JoinedAt = DateTime.UtcNow.AddMinutes(-30),
    };

    /// <summary>The far side of the Google Meet call: CONNECTED from creation until End, no socket.</summary>
    public static TranslationRoomParticipant StandIn(TranslationRoom room) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = room.Id,
        UserId = TranslationRoomConstants.ExternalBridgeParticipantUserId,
        DisplayName = TranslationRoomConstants.ExternalBridgeDisplayName,
        Role = "PARTICIPANT",
        SpeakLanguage = "en",
        ListenLanguage = "en",
        Status = TranslationRoomParticipantStatuses.Connected,
        ConnectionType = "EXTERNAL_BRIDGE",
        JoinedAt = DateTime.UtcNow.AddHours(-1),
    };
}

/// <summary>
/// A Redis double with real get/set/delete semantics for the reapers' "first seen empty" keys. The
/// key IS the grace mechanism, so a mock returning canned values would prove nothing.
/// </summary>
internal sealed class FakeRedisKeys
{
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);

    public IConnectionMultiplexer Multiplexer { get; }

    public FakeRedisKeys()
    {
        var database = new Mock<IDatabase>();

        database
            .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, CommandFlags _) =>
                _keys.TryGetValue(key.ToString()!, out var value) ? (RedisValue)value : RedisValue.Null);
        database
            .Setup(d => d.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey[] keys, CommandFlags _) => keys
                .Select(key => _keys.TryGetValue(key.ToString()!, out var value) ? (RedisValue)value : RedisValue.Null)
                .ToArray());
        // StackExchange.Redis 3.x resolves StringSetAsync(key, value, TimeSpan) to the
        // Expiration/ValueCondition overload (see ReminderNotificationWorkerTests).
        database
            .Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, Expiration _, ValueCondition _, CommandFlags _) =>
            {
                _keys[key.ToString()!] = value.ToString();
                return true;
            });
        database
            .Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, CommandFlags _) => _keys.Remove(key.ToString()!));

        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        Multiplexer = multiplexer.Object;
    }

    public bool Has(string key) => _keys.ContainsKey(key);

    /// <summary>Pretend an earlier tick first saw the room empty <paramref name="ago"/>.</summary>
    public void ObservedEmpty(string key, TimeSpan ago) =>
        _keys[key] = (DateTime.UtcNow - ago).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Pretend the Gateway's room heartbeat has been running unbroken for <paramref name="for"/>.</summary>
    public void HubHeartbeatRunning(TimeSpan @for) =>
        _keys[RoomHubLiveness.HeartbeatKey] =
            (DateTimeOffset.UtcNow - @for).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>Pretend some Gateway replica holds a hub connection on <paramref name="roomId"/>.</summary>
    public void HubSocketOn(Guid roomId) =>
        _keys[RoomHubLiveness.RoomKey(roomId)] =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
}
