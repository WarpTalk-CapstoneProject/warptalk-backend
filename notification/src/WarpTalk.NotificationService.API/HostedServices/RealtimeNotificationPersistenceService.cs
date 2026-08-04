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
        await subscriber.SubscribeAsync(
            RedisChannel.Literal(NotificationConstants.RedisNewNotificationChannel),
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

        logger.LogInformation(
            "Notification center persistence is listening on {Channel}.",
            NotificationConstants.RedisNewNotificationChannel);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
