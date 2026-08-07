using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Redis;

namespace WarpTalk.TranslationRoomService.Tests.Redis;

/// <summary>
/// These three consumers use Redis Streams, not pub/sub, so the earlier SubscribeAsync sweep
/// (TelemetryRedisSubscriberStopHostTests) did not reach them. All three called
/// EnsureConsumerGroupExistsAsync outside every try, and IRedisStreamRepository only swallows
/// BUSYGROUP — so an unreachable Redis threw XGROUP straight out of ExecuteAsync, tripped the
/// default BackgroundServiceExceptionBehavior.StopHost and took the whole
/// TranslationRoomService process down, room CRUD and the gRPC surface included.
///
/// TranslationRoomEventConsumerService was worse still: its group creation sat under a catch
/// that logged Critical and rethrew, so the crash was deliberate.
///
/// Streams counterpart of
/// BillingRedisSubscriberServiceTests.StartAsync_WhenRedisSubscribeFails_DoesNotThrow.
/// </summary>
public sealed class StreamConsumerStopHostTests
{
    [Fact]
    public async Task TranslationRoomEventConsumer_WhenConsumerGroupCreationFails_DoesNotThrow()
    {
        var logger = new Mock<ILogger<TranslationRoomEventConsumerService>>();
        var service = new TranslationRoomEventConsumerService(
            FailingRepository(),
            Mock.Of<IServiceScopeFactory>(),
            logger.Object);

        await StartAndStopAsync(service, logger);
    }

    [Fact]
    public async Task RecordingCompletedEventConsumer_WhenConsumerGroupCreationFails_DoesNotThrow()
    {
        var logger = new Mock<ILogger<RecordingCompletedEventConsumerService>>();
        var service = new RecordingCompletedEventConsumerService(
            FailingRepository(),
            Mock.Of<IServiceScopeFactory>(),
            logger.Object);

        await StartAndStopAsync(service, logger);
    }

    [Fact]
    public async Task WorkspaceEventConsumerWorker_WhenConsumerGroupCreationFails_DoesNotThrow()
    {
        var logger = new Mock<ILogger<WorkspaceEventConsumerWorker>>();
        var service = new WorkspaceEventConsumerWorker(
            FailingRepository(),
            Mock.Of<IServiceProvider>(),
            Mock.Of<IConnectionMultiplexer>(),
            logger.Object);

        await StartAndStopAsync(service, logger);
    }

    /// <summary>
    /// StartAsync returning without throwing is the whole contract: it is what the host observes,
    /// and a throw here is what used to take the process down. The consumer then has to say so —
    /// one that silently never created its group looks healthy while no event is ever processed.
    /// </summary>
    private static async Task StartAndStopAsync<T>(
        Microsoft.Extensions.Hosting.BackgroundService service,
        Mock<ILogger<T>> logger)
    {
        await service.StartAsync(CancellationToken.None);
        await WaitForErrorLogAsync(logger);
        await service.StopAsync(CancellationToken.None);
    }

    private static IRedisStreamRepository FailingRepository()
    {
        var repository = new Mock<IRedisStreamRepository>();
        repository
            .Setup(r => r.EnsureConsumerGroupExistsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
        return repository.Object;
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
