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
        await subscriber.SubscribeAsync(RedisChannel.Literal(RealtimeConstants.RedisChannels.NotificationsNew), async (channel, message) =>
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
        await subscriber.SubscribeAsync(RedisChannel.Literal(RealtimeConstants.RedisChannels.MeetingsEvents), async (channel, message) =>
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
        await subscriber.SubscribeAsync(RedisChannel.Literal(RealtimeConstants.RedisChannels.MeetingStarted), async (channel, message) =>
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
        await subscriber.SubscribeAsync(RedisChannel.Literal(RealtimeConstants.RedisChannels.WorkspaceEvents), async (channel, message) =>
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
                        await _hubContext.Clients.Group(eventType).SendAsync(eventType, root, stoppingToken);
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
        await subscriber.SubscribeAsync(RedisChannel.Literal(RealtimeConstants.RedisChannels.DocumentsEvents), async (channel, message) =>
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
}
