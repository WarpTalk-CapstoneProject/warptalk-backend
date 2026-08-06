using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Infrastructure.Redis;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// An exception escaping ExecuteAsync in a BackgroundService trips the default
/// BackgroundServiceExceptionBehavior.StopHost — not configured away anywhere in this solution —
/// and takes the whole TranscriptService process down, not just this consumer. The app and infra
/// roles deploy in parallel, so reaching the subscribe before Redis accepts connections is a
/// routine startup condition.
///
/// Copy of BillingRedisSubscriberServiceTests.StartAsync_WhenRedisSubscribeFails_DoesNotThrow.
/// </summary>
public sealed class GlossaryStartedEventConsumerStopHostTests
{
    [Fact]
    public async Task StartAsync_WhenRedisSubscribeFails_DoesNotThrow()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        redis.GetSubscriber().Returns(subscriber);
        subscriber
            .SubscribeAsync(
                Arg.Any<RedisChannel>(),
                Arg.Any<Action<RedisChannel, RedisValue>>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromException(
                new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable")));

        var service = new GlossaryStartedEventConsumer(
            redis,
            Substitute.For<IServiceProvider>(),
            new ConfigurationBuilder().Build(),
            Substitute.For<ILogger<GlossaryStartedEventConsumer>>());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
