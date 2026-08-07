using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.MeetingService.API.HostedServices;

namespace WarpTalk.MeetingService.Tests.Workers;

/// <summary>
/// This consumer uses Redis Streams, not pub/sub, so the earlier SubscribeAsync sweep did not
/// reach it. Its XGROUP call only caught BUSYGROUP, so an unreachable Redis threw straight out of
/// ExecuteAsync, tripped the default BackgroundServiceExceptionBehavior.StopHost and took the
/// whole MeetingService process down — even though Redis is otherwise optional for this service.
///
/// Streams counterpart of
/// BillingRedisSubscriberServiceTests.StartAsync_WhenRedisSubscribeFails_DoesNotThrow.
/// </summary>
public sealed class MeetingChatAssistantResultConsumerStopHostTests
{
    [Fact]
    public async Task StartAsync_WhenConsumerGroupCreationFails_DoesNotThrow()
    {
        var logger = new Mock<ILogger<MeetingChatAssistantResultConsumerService>>();
        var service = new MeetingChatAssistantResultConsumerService(
            FailingRedis(),
            Mock.Of<IServiceScopeFactory>(),
            logger.Object);

        await service.StartAsync(CancellationToken.None);
        await WaitForErrorLogAsync(logger);
        await service.StopAsync(CancellationToken.None);
    }

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

    /// <summary>
    /// Surviving startup is only half the contract: a consumer that silently never created its
    /// group looks healthy while no assistant reply reaches the meeting chat.
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
