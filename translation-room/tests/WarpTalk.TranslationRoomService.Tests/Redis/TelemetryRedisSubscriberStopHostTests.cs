using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.Infrastructure.Redis;

namespace WarpTalk.TranslationRoomService.Tests.Redis;

/// <summary>
/// This subscriber used to catch its startup exception, LogCritical and then RETHROW — which is
/// not a guard at all: the rethrow still trips the default BackgroundServiceExceptionBehavior
/// .StopHost and takes the whole TranslationRoomService process down over telemetry, the least
/// critical thing the service does.
///
/// Copy of BillingRedisSubscriberServiceTests.StartAsync_WhenRedisSubscribeFails_DoesNotThrow.
/// </summary>
public sealed class TelemetryRedisSubscriberStopHostTests
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

        var logger = new Mock<ILogger<TelemetryRedisSubscriber>>();
        var service = new TelemetryRedisSubscriber(
            redis.Object,
            Mock.Of<IServiceScopeFactory>(),
            logger.Object);

        await service.StartAsync(CancellationToken.None);
        await WaitForErrorLogAsync(logger);
        await service.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForErrorLogAsync<T>(Mock<ILogger<T>> logger)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                logger.Verify(
                    l => l.Log(
                        LogLevel.Error,
                        It.IsAny<EventId>(),
                        It.IsAny<It.IsAnyType>(),
                        It.IsAny<Exception>(),
                        (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
                    Times.AtLeastOnce);
                return;
            }
            catch (MockException)
            {
                await Task.Delay(10);
            }
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
