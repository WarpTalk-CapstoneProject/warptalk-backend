using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using System.Text.Json;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Shared.Models;

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
        await subscriber.SubscribeAsync(RedisChannel.Literal("warptalk:notifications:new"), async (channel, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty) return;

                var payload = JsonSerializer.Deserialize<RealtimeNotificationMessage>(message.ToString());
                if (payload == null || string.IsNullOrEmpty(payload.UserId)) return;

                if (payload.UserId.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    await _hubContext.Clients.All.SendAsync("NewNotification", payload, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted global NewNotification to all connected clients");
                }
                else
                {
                    var userGroupName = $"user:{payload.UserId}";
                    await _hubContext.Clients.Group(userGroupName).SendAsync("NewNotification", payload, stoppingToken);

                    var workspaceGroupName = $"workspace:{payload.UserId}";
                    await _hubContext.Clients.Group(workspaceGroupName).SendAsync("NewNotification", payload, stoppingToken);

                    // Forward billing updates to the admin:billing group for real-time admin monitoring
                    if (payload.Type != null && payload.Type.StartsWith("billing.", StringComparison.OrdinalIgnoreCase))
                    {
                        await _hubContext.Clients.Group("admin:billing").SendAsync("NewNotification", payload, stoppingToken);
                    }

                    _logger.LogDebug("RedisSubscriber: Broadcasted NewNotification to {UserGroupName}, {WorkspaceGroupName} and admin:billing", userGroupName, workspaceGroupName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process incoming Redis notification message.");
            }
        });

        _logger.LogInformation("NotificationRedisSubscriberService started listening to 'warptalk:notifications:new'.");
    }
}
