using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// Every BackgroundService in this process subscribes to Redis in ExecuteAsync. An exception
/// escaping ExecuteAsync trips the default BackgroundServiceExceptionBehavior.StopHost — which is
/// not configured away anywhere in this solution — and kills the whole Gateway: YARP, every hub,
/// every health endpoint. Because the app and infra roles deploy in parallel, "Redis is not
/// accepting connections yet" is a routine startup condition, not an exotic one.
///
/// These are the Gateway's copies of BillingRedisSubscriberServiceTests
/// .StartAsync_WhenRedisSubscribeFails_DoesNotThrow.
/// </summary>
public sealed class RedisSubscriberStopHostResilienceTests
{
    [Fact]
    public async Task NotificationSubscriber_WhenRedisSubscribeFails_DoesNotThrow()
    {
        var logger = new Mock<ILogger<NotificationRedisSubscriberService>>();
        var service = new NotificationRedisSubscriberService(
            FailingRedis(),
            HubContext<NotificationHub>(),
            logger.Object);

        await StartAndStopAsync(service, logger);
    }

    [Fact]
    public async Task BillingSubscriber_WhenRedisSubscribeFails_DoesNotThrow()
    {
        var logger = new Mock<ILogger<BillingRedisSubscriberService>>();
        var service = new BillingRedisSubscriberService(
            FailingRedis(),
            HubContext<BillingHub>(),
            logger.Object);

        await StartAndStopAsync(service, logger);
    }

    [Fact]
    public async Task TranslationRoomSubscriber_WhenRedisSubscribeFails_DoesNotThrow()
    {
        var logger = new Mock<ILogger<TranslationRoomRedisSubscriberService>>();
        var service = new TranslationRoomRedisSubscriberService(
            FailingRedis(),
            HubContext<TranslationRoomHub>(),
            logger.Object);

        await StartAndStopAsync(service, logger);
    }

    /// <summary>
    /// StartAsync returning without throwing is the whole contract: it is what the host observes,
    /// and a throw here is what used to take the process down. The service then has to say so —
    /// degrading quietly would be its own bug, because a Gateway that survives startup but never
    /// subscribed looks perfectly healthy while no realtime event ever reaches a client.
    /// </summary>
    private static async Task StartAndStopAsync<T>(BackgroundService service, Mock<ILogger<T>> logger)
    {
        await service.StartAsync(CancellationToken.None);
        await WaitForErrorLogAsync(logger);
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The subscribe failure is observed on the background task, so it can land just after
    /// StartAsync returns. Poll rather than sleep a fixed interval.
    /// </summary>
    private static async Task WaitForErrorLogAsync<T>(Mock<ILogger<T>> logger)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (logger.Invocations.Count > 0)
            {
                VerifyFailureWasLogged(logger);
                return;
            }

            await Task.Delay(10);
        }

        VerifyFailureWasLogged(logger);
    }

    private static IConnectionMultiplexer FailingRedis()
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

        return redis.Object;
    }

    private static IHubContext<THub> HubContext<THub>() where THub : Hub
    {
        var hubContext = new Mock<IHubContext<THub>>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());
        clients.Setup(c => c.All).Returns(Mock.Of<IClientProxy>());
        hubContext.Setup(c => c.Clients).Returns(clients.Object);
        return hubContext.Object;
    }

    /// <summary>
    /// Degrading quietly would be its own bug: a Gateway that survives startup but never
    /// subscribed looks perfectly healthy while no realtime event ever reaches a client.
    /// </summary>
    private static void VerifyFailureWasLogged<T>(Mock<ILogger<T>> logger) =>
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.AtLeastOnce);
}
