using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// AiResultConsumerService consumes Redis *Streams*, not pub/sub, so the earlier
/// SubscribeAsync sweep (RedisSubscriberStopHostResilienceTests) did not cover it. Its four
/// EnsureConsumerGroupAsync calls sat outside every try, and ExecuteAsync only catches
/// OperationCanceledException — so an unreachable Redis threw XGROUP straight out of
/// ExecuteAsync, tripped the default BackgroundServiceExceptionBehavior.StopHost and took the
/// whole Gateway down: YARP proxying, every hub, every health endpoint. The app and infra roles
/// deploy in parallel, so reaching this before Redis accepts connections is routine.
///
/// This is the Streams counterpart of
/// BillingRedisSubscriberServiceTests.StartAsync_WhenRedisSubscribeFails_DoesNotThrow.
/// </summary>
public sealed class AiResultConsumerStopHostTests
{
    [Fact]
    public async Task StartAsync_WhenConsumerGroupCreationFails_DoesNotThrow()
    {
        var logger = new Mock<ILogger<AiResultConsumerService>>();
        var service = BuildService(logger);

        // StartAsync returning without throwing is the whole contract: it is what the host
        // observes, and a throw here is what used to take the process down.
        await service.StartAsync(CancellationToken.None);
        await WaitForErrorLogAsync(logger);
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Surviving startup is only half of it. A gateway that never created its consumer groups
    /// looks perfectly healthy while no transcript, translation, audio or assistant result ever
    /// reaches a client — so the failure has to be visible in the log.
    /// </summary>
    [Fact]
    public async Task StartAsync_WhenConsumerGroupCreationFails_LogsWhichStreamIsNotBeingDelivered()
    {
        var logger = new Mock<ILogger<AiResultConsumerService>>();
        var service = BuildService(logger);

        await service.StartAsync(CancellationToken.None);
        await WaitForErrorLogAsync(logger);
        await service.StopAsync(CancellationToken.None);

        var logged = logger.Invocations
            .Where(invocation => invocation.Arguments.Count > 2)
            .Select(invocation => invocation.Arguments[2]?.ToString() ?? string.Empty)
            .ToList();

        Assert.Contains(logged, message => message.Contains("NOT reaching clients", StringComparison.Ordinal));
    }

    private static AiResultConsumerService BuildService(Mock<ILogger<AiResultConsumerService>> logger)
    {
        var streamService = new RedisStreamService(
            FailingRedis(),
            Mock.Of<ILogger<RedisStreamService>>(),
            new ConfigurationBuilder().Build());

        return new AiResultConsumerService(
            streamService,
            new ActiveTranslationRoomRegistry(),
            HubContext<TranslationRoomHub>(),
            // Never reached: group creation fails before any room policy lookup happens.
            null!,
            null!,
            logger.Object);
    }

    /// <summary>
    /// A multiplexer that hands out a database whose XGROUP throws, which is what
    /// abortConnect=false produces while Redis is still unreachable.
    /// </summary>
    private static IConnectionMultiplexer FailingRedis()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var database = new Mock<IDatabase>();

        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        database
            .Setup(d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<bool>(),
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
    /// The failure is observed on the background task, so it can land just after StartAsync
    /// returns. Poll rather than sleep a fixed interval.
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
