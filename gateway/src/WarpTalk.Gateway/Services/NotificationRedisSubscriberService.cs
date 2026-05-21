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

                var groupName = $"user:{payload.UserId}";
                await _hubContext.Clients.Group(groupName).SendAsync("NewNotification", payload, stoppingToken);

                _logger.LogDebug("RedisSubscriber: Broadcasted NewNotification to {GroupName}", groupName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process incoming Redis notification message.");
            }
        });

        _logger.LogInformation("NotificationRedisSubscriberService started listening to 'warptalk:notifications:new'.");
    }
}
