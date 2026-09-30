using WarpTalk.Gateway.Constants;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Which gateway replica relays Redis pub/sub events into SignalR.
///
/// WHY: every gateway pod subscribes to the same pub/sub channels, and every hub send goes through
/// the Redis SignalR backplane, which already delivers one <c>Clients.Group(...)</c> send to the
/// matching connections on EVERY pod. With N gateway pods each event reached each client N times.
///
/// HOW: all pods stay subscribed (hot standby) but only the leader elected on
/// <see cref="LeaseResource"/> forwards to the hub (<see cref="IPubSubLeadership.ShouldHandle"/>
/// at the top of every handler); the backplane then carries that single send to every pod's
/// clients. Failover guarantees are on <see cref="IPubSubLeadership"/> and
/// <see cref="LeaderElector"/>.
///
/// NOT needed for <see cref="AiResultConsumerService"/>: it reads Redis Streams through one shared
/// consumer group, which already hands each entry to exactly one pod.
/// </summary>
public static class RealtimeRelay
{
    /// <summary>The lease the gateway pods compete for.</summary>
    public const string LeaseResource = "gateway:realtime-relay";

    public static string Key(string subscriberName, string channel) => $"{subscriberName}|{channel}";

    /// <summary>Every (subscriber, channel) pair that must be live before a pod may lead.</summary>
    public static readonly IReadOnlyCollection<string> RequiredSubscriptions =
    [
        Key(nameof(NotificationRedisSubscriberService), RealtimeConstants.RedisChannels.NotificationsNew),
        Key(nameof(NotificationRedisSubscriberService), RealtimeConstants.RedisChannels.MeetingsEvents),
        Key(nameof(NotificationRedisSubscriberService), RealtimeConstants.RedisChannels.MeetingStarted),
        Key(nameof(NotificationRedisSubscriberService), RealtimeConstants.RedisChannels.WorkspaceEvents),
        Key(nameof(NotificationRedisSubscriberService), RealtimeConstants.RedisChannels.DocumentsEvents),
        Key(nameof(BillingRedisSubscriberService), RealtimeConstants.RedisChannels.NotificationsNew),
        Key(nameof(TranslationRoomRedisSubscriberService), RealtimeConstants.RedisChannels.TranslationRoomCommands),
    ];
}
