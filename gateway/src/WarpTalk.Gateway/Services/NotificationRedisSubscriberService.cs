using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using System.Text.Json;
using WarpTalk.Gateway.Constants;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Shared.Models;
using WarpTalk.Shared.Events;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Background service acting as a Redis Pub/Sub subscriber.
/// Listens for new notifications from the Notification Service 
/// and broadcasts them in real-time to the appropriate user's SignalR group.
/// </summary>
public class NotificationRedisSubscriberService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<NotificationRedisSubscriberService> _logger;

    public NotificationRedisSubscriberService(
        IConnectionMultiplexer redis,
        IHubContext<NotificationHub> hubContext,
        ILogger<NotificationRedisSubscriberService> logger)
    {
        _redis = redis;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();

        // 1. Listen for personal user notifications (e.g. invites, system alerts)
        await SubscribeWithRetryAsync(subscriber, RealtimeConstants.RedisChannels.NotificationsNew, stoppingToken, async (channel, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty) return;

                var payload = JsonSerializer.Deserialize<RealtimeNotificationMessage>(message.ToString());
                if (payload == null || string.IsNullOrEmpty(payload.UserId)) return;

                if (payload.UserId.Equals("all", StringComparison.OrdinalIgnoreCase) || payload.UserId.Equals("*", StringComparison.OrdinalIgnoreCase))
                {
                    await _hubContext.Clients.All.SendAsync(RealtimeConstants.ClientMethods.NewNotification, payload, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted NewNotification to ALL clients");
                }
                else
                {
                    var groupName = RealtimeConstants.Groups.User(payload.UserId);
                    await _hubContext.Clients.Group(groupName).SendAsync(RealtimeConstants.ClientMethods.NewNotification, payload, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted NewNotification to {GroupName}", groupName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process incoming Redis notification message.");
            }
        });

        // 2. Listen for workspace & meeting real-time status events
        await SubscribeWithRetryAsync(subscriber, RealtimeConstants.RedisChannels.MeetingsEvents, stoppingToken, async (channel, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty) return;

                using var doc = JsonDocument.Parse(message.ToString());
                var root = doc.RootElement;

                var eventType = root.TryGetProperty("eventType", out var et) ? et.GetString() : "MeetingUpdated";
                var workspaceId = root.TryGetProperty("workspaceId", out var ws) ? ws.GetString() : null;
                var userId = root.TryGetProperty("userId", out var u) ? u.GetString() : null;

                if (!string.IsNullOrEmpty(workspaceId))
                {
                    var wsGroup = RealtimeConstants.Groups.Workspace(workspaceId);
                    await _hubContext.Clients.Group(wsGroup).SendAsync(RealtimeConstants.ClientMethods.MeetingEvent, root, stoppingToken);
                    if (!string.IsNullOrEmpty(eventType))
                    {
                        await _hubContext.Clients.Group(wsGroup).SendAsync(eventType, root, stoppingToken);
                    }
                    _logger.LogDebug("RedisSubscriber: Broadcasted {EventType} to {GroupName}", eventType, wsGroup);
                }

                if (!string.IsNullOrEmpty(userId))
                {
                    var userGroup = RealtimeConstants.Groups.User(userId);
                    await _hubContext.Clients.Group(userGroup).SendAsync(RealtimeConstants.ClientMethods.MeetingEvent, root, stoppingToken);
                    if (!string.IsNullOrEmpty(eventType))
                    {
                        await _hubContext.Clients.Group(userGroup).SendAsync(eventType, root, stoppingToken);
                    }
                    _logger.LogDebug("RedisSubscriber: Broadcasted {EventType} to {GroupName}", eventType, userGroup);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process incoming Redis meeting event message.");
            }
        });

        // 3. Listen for meeting.started event from Workspace/Meeting microservices
        await SubscribeWithRetryAsync(subscriber, RealtimeConstants.RedisChannels.MeetingStarted, stoppingToken, async (channel, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty) return;
                var envelope =
                    JsonSerializer.Deserialize<EventEnvelope<MeetingStartedEventPayload>>(
                        message.ToString());
                if (envelope?.EventType == MeetingEventTypes.Started &&
                    envelope.SchemaVersion == DomainEventEnvelope.CurrentSchemaVersion &&
                    envelope.Payload.WorkspaceId != Guid.Empty)
                {
                    var workspaceId = envelope.Payload.WorkspaceId.ToString();
                    var wsGroup = RealtimeConstants.Groups.Workspace(workspaceId);
                    await _hubContext.Clients.Group(wsGroup).SendAsync(
                        RealtimeConstants.ClientMethods.MeetingStarted,
                        envelope.Payload,
                        stoppingToken);
                    await _hubContext.Clients.Group(wsGroup).SendAsync(
                        RealtimeConstants.ClientMethods.MeetingStatusChanged,
                        envelope.Payload,
                        stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted MeetingStarted to {GroupName}", wsGroup);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process incoming Redis meeting.started message.");
            }
        });

        // 4. Listen for workspace-level events (MemberRoleUpdated, MemberRemoved, Presence, AI Summary Progress)
        await SubscribeWithRetryAsync(subscriber, RealtimeConstants.RedisChannels.WorkspaceEvents, stoppingToken, async (channel, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty) return;
                using var doc = JsonDocument.Parse(message.ToString());
                var root = doc.RootElement;

                var eventType = root.TryGetProperty("eventType", out var et) ? et.GetString() : "WorkspaceEvent";
                var workspaceId = root.TryGetProperty("workspaceId", out var ws) ? ws.GetString() : null;
                var userId = root.TryGetProperty("userId", out var u) ? u.GetString() : null;

                if (!string.IsNullOrEmpty(workspaceId))
                {
                    var wsGroup = RealtimeConstants.Groups.Workspace(workspaceId);
                    await _hubContext.Clients.Group(wsGroup).SendAsync(RealtimeConstants.ClientMethods.WorkspaceEvent, root, stoppingToken);
                    if (!string.IsNullOrEmpty(eventType))
                    {
                        await _hubContext.Clients.Group(wsGroup).SendAsync(eventType, root, stoppingToken);
                    }
                    _logger.LogDebug("RedisSubscriber: Broadcasted {EventType} to {GroupName}", eventType, wsGroup);
                }

                if (!string.IsNullOrEmpty(userId))
                {
                    var userGroup = RealtimeConstants.Groups.User(userId);
                    await _hubContext.Clients.Group(userGroup).SendAsync(RealtimeConstants.ClientMethods.WorkspaceEvent, root, stoppingToken);
                    if (!string.IsNullOrEmpty(eventType))
                    {
                        // Group(userGroup), not Group(eventType): the latter addressed a group
                        // named after the event ("MemberRoleUpdated", …) that nobody ever joins,
                        // so user-scoped workspace events were silently dropped. The other three
                        // handlers in this file always got this right.
                        await _hubContext.Clients.Group(userGroup).SendAsync(eventType, root, stoppingToken);
                    }
                    _logger.LogDebug("RedisSubscriber: Broadcasted {EventType} to {GroupName}", eventType, userGroup);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process incoming Redis workspace event message.");
            }
        });

        // 5. Listen for document life-cycle & status events
        await SubscribeWithRetryAsync(subscriber, RealtimeConstants.RedisChannels.DocumentsEvents, stoppingToken, async (channel, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty) return;
                using var doc = JsonDocument.Parse(message.ToString());
                var root = doc.RootElement;

                var eventType = root.TryGetProperty("eventType", out var et) ? et.GetString() : "DocumentStatusChanged";
                var workspaceId = root.TryGetProperty("workspaceId", out var ws) ? ws.GetString() : null;
                var userId = root.TryGetProperty("userId", out var u) ? u.GetString() : null;

                if (!string.IsNullOrEmpty(workspaceId))
                {
                    var wsGroup = RealtimeConstants.Groups.Workspace(workspaceId);
                    await _hubContext.Clients.Group(wsGroup).SendAsync(RealtimeConstants.ClientMethods.DocumentStatusChanged, root, stoppingToken);
                    if (!string.IsNullOrEmpty(eventType))
                    {
                        await _hubContext.Clients.Group(wsGroup).SendAsync(eventType, root, stoppingToken);
                    }
                    _logger.LogDebug("RedisSubscriber: Broadcasted document event {EventType} to {GroupName}", eventType, wsGroup);
                }

                if (!string.IsNullOrEmpty(userId))
                {
                    var userGroup = RealtimeConstants.Groups.User(userId);
                    await _hubContext.Clients.Group(userGroup).SendAsync(RealtimeConstants.ClientMethods.DocumentStatusChanged, root, stoppingToken);
                    if (!string.IsNullOrEmpty(eventType))
                    {
                        await _hubContext.Clients.Group(userGroup).SendAsync(eventType, root, stoppingToken);
                    }
                    _logger.LogDebug("RedisSubscriber: Broadcasted document event {EventType} to {GroupName}", eventType, userGroup);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process incoming Redis document event message.");
            }
        });

        _logger.LogInformation("NotificationRedisSubscriberService started listening to notifications, meeting events, workspace events, and document events.");
    }

    /// <summary>
    /// Subscribes with bounded backoff instead of letting the exception escape.
    ///
    /// An exception out of <see cref="ExecuteAsync"/> in a BackgroundService trips the default
    /// BackgroundServiceExceptionBehavior.StopHost, which for the Gateway means the whole
    /// application dies — YARP, every hub, every health endpoint — because Redis was a second
    /// late accepting connections. The app and infra roles deploy in parallel, so that race is
    /// routine. Same shape as HostFallbackConsumerWorker / ParticipantOfflineConsumerWorker /
    /// EntitlementsChangedConsumer, which each took this outage before being guarded.
    ///
    /// Retried per channel rather than around the whole set: re-running a batch after a partial
    /// failure would re-register the handlers that already succeeded and double-deliver every
    /// message on those channels.
    /// </summary>
    private async Task SubscribeWithRetryAsync(
        ISubscriber subscriber,
        string channel,
        CancellationToken stoppingToken,
        Action<RedisChannel, RedisValue> handler)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(RedisChannel.Literal(channel), handler);
                _logger.LogInformation("NotificationRedisSubscriberService subscribed to '{Channel}'.", channel);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "NotificationRedisSubscriberService could not subscribe to '{Channel}'; retrying in {RetryDelay}. "
                    + "Realtime delivery on this channel is down until it succeeds.",
                    channel,
                    retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }
}
