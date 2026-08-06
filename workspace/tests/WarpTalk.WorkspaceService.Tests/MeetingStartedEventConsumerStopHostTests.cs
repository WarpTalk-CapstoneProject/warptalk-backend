using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// An exception escaping ExecuteAsync in a BackgroundService trips the default
/// BackgroundServiceExceptionBehavior.StopHost — not configured away anywhere in this solution —
/// and takes the whole WorkspaceService process down, not just this consumer. That is the outage
/// EntitlementsChangedConsumer in this very assembly already documents; this consumer sat
/// unguarded next to it.
///
/// Copy of BillingRedisSubscriberServiceTests.StartAsync_WhenRedisSubscribeFails_DoesNotThrow.
/// </summary>
public sealed class MeetingStartedEventConsumerStopHostTests
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

        var service = new MeetingStartedEventConsumer(
            redis,
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<MeetingStartedEventConsumer>>());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
