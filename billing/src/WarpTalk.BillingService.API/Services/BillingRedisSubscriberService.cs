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
using WarpTalk.Shared.Coordination;
using WarpTalk.Shared.Models;

namespace WarpTalk.BillingService.API.Services;

/// <summary>
/// Bridges billing Redis notifications to clients connected directly to the Billing service hub.
///
/// Every billing replica subscribes, but only the elected leader forwards
/// (<see cref="IPubSubLeadership"/>): with the SignalR backplane one group send already reaches
/// every replica's connections, so N forwarding replicas meant N copies per client.
/// </summary>
public class BillingRedisSubscriberService : BackgroundService
{
    /// <summary>The lease billing replicas compete for to relay pub/sub into SignalR.</summary>
    public const string LeaseResource = "billing:realtime-relay";

    /// <summary>The one subscription a replica must hold before it may lead.</summary>
    public const string SubscriptionKey = BillingMessageConstants.Notifications.Channel;

    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<BillingHub> _hubContext;
    private readonly ILogger<BillingRedisSubscriberService> _logger;
    private readonly IPubSubLeadership _leadership;

    /// <summary>
    /// Creates a Redis-to-SignalR bridge for billing notifications.
    /// </summary>
    public BillingRedisSubscriberService(
        IConnectionMultiplexer redis,
        IHubContext<BillingHub> hubContext,
        ILogger<BillingRedisSubscriberService> logger,
        IPubSubLeadership leadership)
    {
        _redis = redis;
        _hubContext = hubContext;
        _logger = logger;
        _leadership = leadership;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retries with bounded backoff: this used to give up after one failed subscribe and leave
        // realtime billing off until the next restart, which a parallel app/infra deploy makes routine.
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(BillingMessageConstants.Notifications.Channel), async (_, message) =>
                {
                    try
                    {
                        if (!_leadership.ShouldHandle)
                            return;

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

                _leadership.MarkSubscribed(SubscriptionKey);
                _logger.LogInformation("BillingRedisSubscriberService started listening to {Channel}.", BillingMessageConstants.Notifications.Channel);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "BillingRedisSubscriberService could not subscribe to {Channel}; retrying in {RetryDelay}.",
                    BillingMessageConstants.Notifications.Channel,
                    retryDelay);
                try
                {
                    await Task.Delay(retryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }
}
