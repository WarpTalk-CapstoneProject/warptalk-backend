using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.NotificationService.API.HostedServices;

namespace WarpTalk.NotificationService.Tests.API.HostedServices;

/// <summary>
/// An exception escaping ExecuteAsync in a BackgroundService trips the default
/// BackgroundServiceExceptionBehavior.StopHost — not configured away anywhere in this solution —
/// and takes the whole NotificationService process down, not just this listener. Because the app
/// and infra roles deploy in parallel, reaching the subscribe before Redis accepts connections is
/// a routine startup condition.
///
/// Copy of BillingRedisSubscriberServiceTests.StartAsync_WhenRedisSubscribeFails_DoesNotThrow.
/// </summary>
public sealed class RealtimeNotificationPersistenceServiceStopHostTests
{
    [Fact]
    public async Task StartAsync_WhenRedisSubscribeFails_DoesNotThrow()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var subscriber = new Mock<ISubscriber>();
        redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        subscriber
            .Setup(s => s.SubscribeAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));

        var logger = new Mock<ILogger<RealtimeNotificationPersistenceService>>();
        var service = new RealtimeNotificationPersistenceService(
            redis.Object,
            Mock.Of<IServiceScopeFactory>(),
            logger.Object);

        await service.StartAsync(CancellationToken.None);
        await WaitForErrorLogAsync(logger);
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Surviving startup is only half the contract. A service that silently never subscribed
    /// looks healthy while the notification centre never fills, so the failure must be logged.
    /// The failure is observed on the background task, so poll rather than sleep a fixed interval.
    /// </summary>
    private static async Task WaitForErrorLogAsync<T>(Mock<ILogger<T>> logger)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && logger.Invocations.Count == 0)
        {
            await Task.Delay(10);
        }

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.AtLeastOnce);
    }
}
