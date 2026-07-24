using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.BillingService.API.Hubs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared.Models;

namespace WarpTalk.BillingService.API.Services;

/// <summary>
/// Bridges billing Redis notifications to clients connected directly to the Billing service hub.
/// </summary>
public class BillingRedisSubscriberService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<BillingHub> _hubContext;
    private readonly ILogger<BillingRedisSubscriberService> _logger;

    /// <summary>
    /// Creates a Redis-to-SignalR bridge for billing notifications.
    /// </summary>
    public BillingRedisSubscriberService(
        IConnectionMultiplexer redis,
        IHubContext<BillingHub> hubContext,
        ILogger<BillingRedisSubscriberService> logger)
    {
        _redis = redis;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(BillingMessageConstants.Notifications.Channel), async (_, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty)
                    return;

                var payload = JsonSerializer.Deserialize<RealtimeNotificationMessage>(message.ToString());
                if (payload == null || string.IsNullOrEmpty(payload.UserId))
                    return;

                if (string.IsNullOrEmpty(payload.Type) || !payload.Type.StartsWith(BillingMessageConstants.Notifications.TypePrefix, StringComparison.OrdinalIgnoreCase))
                    return;

                await _hubContext.Clients
                    .Group(BillingHub.UserGroupName(payload.UserId))
                    .SendAsync(BillingMessageConstants.Notifications.HubEvents.BillingNotification, payload, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToProcessRedisBillingNotification);
            }
        });

        _logger.LogInformation("BillingRedisSubscriberService started listening to {Channel}.", BillingMessageConstants.Notifications.Channel);
    }
}
