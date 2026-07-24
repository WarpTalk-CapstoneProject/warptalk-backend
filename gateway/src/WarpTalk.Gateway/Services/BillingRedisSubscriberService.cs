using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Gateway.Constants;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Shared.Models;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Background service acting as a Redis Pub/Sub subscriber for billing notifications.
/// Listens to 'warptalk:notifications:new' channel, filters for billing events,
/// and broadcasts them to clients connected to the Gateway's BillingHub.
/// </summary>
public class BillingRedisSubscriberService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<BillingHub> _hubContext;
    private readonly ILogger<BillingRedisSubscriberService> _logger;

    public BillingRedisSubscriberService(
        IConnectionMultiplexer redis,
        IHubContext<BillingHub> hubContext,
        ILogger<BillingRedisSubscriberService> _logger)
    {
        _redis = redis;
        _hubContext = hubContext;
        this._logger = _logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(GatewayBillingConstants.NotificationChannel), async (channel, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty) return;

                var payload = JsonSerializer.Deserialize<RealtimeNotificationMessage>(message.ToString());
                if (payload == null || string.IsNullOrEmpty(payload.UserId)) return;

                // Only broadcast billing-related notifications
                if (string.IsNullOrEmpty(payload.Type) || !payload.Type.StartsWith(GatewayBillingConstants.NotificationTypePrefix, StringComparison.OrdinalIgnoreCase))
                    return;

                var groupName = BillingHub.UserGroupName(payload.UserId);
                await _hubContext.Clients.Group(groupName).SendAsync(GatewayBillingConstants.BillingNotificationEvent, payload, stoppingToken);

                _logger.LogDebug(GatewayBillingConstants.BroadcastLogTemplate, GatewayBillingConstants.BillingNotificationEvent, payload.Type, groupName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, GatewayBillingConstants.ProcessRedisNotificationError);
            }
        });

        _logger.LogInformation(GatewayBillingConstants.SubscriberStartedTemplate, GatewayBillingConstants.NotificationChannel);
    }
}
