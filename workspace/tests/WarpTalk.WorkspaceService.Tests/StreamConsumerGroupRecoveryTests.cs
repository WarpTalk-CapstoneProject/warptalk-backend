using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// These two Streams consumers already caught everything around XGROUP, so they never tripped
/// BackgroundServiceExceptionBehavior.StopHost — but they swallowed the failure once and never
/// retried. After a Redis outage at startup the group was never created, so every subsequent
/// StreamReadGroupAsync failed NOGROUP forever: the service ran deaf while looking perfectly
/// alive. For the DLP/PII guardrail that means uploaded documents were never scanned.
///
/// The contract is therefore both halves: do not throw, and do not silently give up.
/// </summary>
public sealed class StreamConsumerGroupRecoveryTests
{
    [Fact]
    public async Task DocumentEmbeddingIndexResultConsumer_WhenConsumerGroupCreationFails_DoesNotThrowAndKeepsRetrying()
    {
        var logger = Substitute.For<ILogger<DocumentEmbeddingIndexResultConsumerService>>();
        var redis = FailingRedis(out var database);
        var service = new DocumentEmbeddingIndexResultConsumerService(
            redis,
            Substitute.For<IServiceProvider>(),
            logger);

        await service.StartAsync(CancellationToken.None);
        await WaitForAttemptsAsync(database, minimumAttempts: 2);
        await service.StopAsync(CancellationToken.None);

        Assert.True(
            AttemptCount(database) >= 2,
            "The consumer must retry group creation, not swallow the failure and run deaf forever.");
    }

    [Fact]
    public async Task DocumentSecurityGuardrailConsumer_WhenConsumerGroupCreationFails_DoesNotThrowAndKeepsRetrying()
    {
        var logger = Substitute.For<ILogger<DocumentSecurityGuardrailConsumerService>>();
        var redis = FailingRedis(out var database);
        var service = new DocumentSecurityGuardrailConsumerService(
            redis,
            logger,
            Substitute.For<IServiceProvider>());

        await service.StartAsync(CancellationToken.None);
        await WaitForAttemptsAsync(database, minimumAttempts: 2);
        await service.StopAsync(CancellationToken.None);

        Assert.True(
            AttemptCount(database) >= 2,
            "The guardrail must retry group creation; running deaf means documents go unscanned.");
    }

    private static IConnectionMultiplexer FailingRedis(out IDatabase database)
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        database = Substitute.For<IDatabase>();

        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database
            .StreamCreateConsumerGroupAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<RedisValue?>(),
                Arg.Any<bool>(),
                Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));

        return redis;
    }

    private static int AttemptCount(IDatabase database) =>
        database.ReceivedCalls().Count(call =>
            call.GetMethodInfo().Name == nameof(IDatabase.StreamCreateConsumerGroupAsync));

    /// <summary>
    /// First retry is scheduled 2s out, so poll rather than sleep a fixed interval.
    /// </summary>
    private static async Task WaitForAttemptsAsync(IDatabase database, int minimumAttempts)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && AttemptCount(database) < minimumAttempts)
        {
            await Task.Delay(50);
        }
    }
}
