using System.Text.Json;
using StackExchange.Redis;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.Shared.Models;

namespace WarpTalk.NotificationService.API.HostedServices;

/// <summary>
/// Persists direct Redis notification publishers so the bell is a durable center,
/// not merely a transient toast feed.
/// </summary>
public sealed class RealtimeNotificationPersistenceService(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    ILogger<RealtimeNotificationPersistenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();
        await SubscribeWithRetryAsync(
            subscriber,
            stoppingToken,
            async (_, value) =>
            {
                if (value.IsNullOrEmpty || stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var message = JsonSerializer.Deserialize<RealtimeNotificationMessage>(
                        value.ToString(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (message is null)
                    {
                        return;
                    }

                    using var scope = scopeFactory.CreateScope();
                    var handler = scope.ServiceProvider
                        .GetRequiredService<RealtimeNotificationPersistenceHandler>();
                    await handler.HandleAsync(message, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to persist realtime notification for the notification center.");
                }
            });

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Subscribes with bounded backoff instead of letting the exception escape.
    ///
    /// An exception out of <see cref="ExecuteAsync"/> in a BackgroundService trips the default
    /// BackgroundServiceExceptionBehavior.StopHost and takes the ENTIRE NotificationService
    /// process down — not just this listener. The app and infra roles deploy in parallel, so
    /// reaching this line before Redis is accepting connections is routine, and an unguarded
    /// subscribe turns a transient Redis blip into a failed deploy. Same bounded-backoff shape
    /// as HostFallbackConsumerWorker / ParticipantOfflineConsumerWorker / EntitlementsChangedConsumer.
    ///
    /// A failed subscribe is never silent: it logs at Error every attempt, so a service that ends
    /// up running deaf says so rather than looking healthy while the bell never fills.
    /// </summary>
    private async Task SubscribeWithRetryAsync(
        ISubscriber subscriber,
        CancellationToken stoppingToken,
        Action<RedisChannel, RedisValue> handler)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(
                    RedisChannel.Literal(NotificationConstants.RedisNewNotificationChannel),
                    handler);

                logger.LogInformation(
                    "Notification center persistence is listening on {Channel}.",
                    NotificationConstants.RedisNewNotificationChannel);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(
                    ex,
                    "RealtimeNotificationPersistenceService could not subscribe to '{Channel}'; retrying in {RetryDelay}. "
                    + "Notifications are not being persisted to the notification center until it succeeds.",
                    NotificationConstants.RedisNewNotificationChannel,
                    retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }
}
