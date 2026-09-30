using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Redis;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

public sealed class RecordingCompletedEventConsumerServiceTests
{
    [Fact]
    public async Task ConsumeBatchAsync_AcknowledgesOnlyAfterSuccessfulProcessing()
    {
        var repository = CreateRepositoryWithOneNewMessage();
        var handler = new Mock<IRecordingCompletedStreamMessageHandler>();
        handler.Setup(service => service.HandleAsync(
                It.IsAny<RedisStreamMessage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var sut = CreateService(repository.Object, handler.Object);

        await sut.ConsumeBatchAsync("consumer-1", CancellationToken.None);

        repository.Verify(service => service.AcknowledgeAsync(
            RecordingCompletedEventConsumerService.StreamName,
            RecordingCompletedEventConsumerService.GroupName,
            "1-0"), Times.Once);
    }

    [Fact]
    public async Task ConsumeBatchAsync_RetriesThenDlqsBeforeAcknowledging()
    {
        var repository = CreateRepositoryWithOneNewMessage();
        var handler = new Mock<IRecordingCompletedStreamMessageHandler>();
        handler.Setup(service => service.HandleAsync(
                It.IsAny<RedisStreamMessage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("database unavailable"));
        var sut = CreateService(repository.Object, handler.Object);

        await sut.ConsumeBatchAsync("consumer-1", CancellationToken.None);

        handler.Verify(service => service.HandleAsync(
            It.IsAny<RedisStreamMessage>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        repository.Verify(service => service.AddAsync(
            RecordingCompletedEventConsumerService.DlqStreamName,
            It.Is<Dictionary<string, string>>(values =>
                values["original_message_id"] == "1-0" &&
                values["error_message"] == "database unavailable")), Times.Once);
        repository.Verify(service => service.AcknowledgeAsync(
            RecordingCompletedEventConsumerService.StreamName,
            RecordingCompletedEventConsumerService.GroupName,
            "1-0"), Times.Once);
    }

    /// <summary>
    /// rec-loss: nothing reads the DLQ stream on its own, so a dead-lettered recording event used to
    /// be a recording that vanished without a word. It is now said at Error, with the ids an
    /// operator needs to find the meeting.
    /// </summary>
    [Fact]
    public async Task ConsumeBatchAsync_LogsAnErrorNamingTheEgress_BeforeDeadLettering()
    {
        var repository = CreateRepositoryWithOneNewMessage(
            "meeting.recording_failed",
            """{"payload":{"translation_room_id":"0199a000-0000-7000-8000-000000000001","egress_id":"EG_LOST"}}""");
        var handler = new Mock<IRecordingCompletedStreamMessageHandler>();
        handler.Setup(service => service.HandleAsync(
                It.IsAny<RedisStreamMessage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("database unavailable"));
        var logger = new Mock<ILogger<RecordingCompletedEventConsumerService>>();
        var sut = CreateService(repository.Object, handler.Object, logger.Object);

        await sut.ConsumeBatchAsync("consumer-1", CancellationToken.None);

        logger.Verify(log => log.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) =>
                state.ToString()!.Contains("meeting.recording_failed") &&
                state.ToString()!.Contains("1-0") &&
                state.ToString()!.Contains("EG_LOST") &&
                state.ToString()!.Contains("0199a000-0000-7000-8000-000000000001") &&
                state.ToString()!.Contains("database unavailable")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task ConsumeBatchAsync_DoesNotAcknowledge_WhenDlqWriteFails()
    {
        var repository = CreateRepositoryWithOneNewMessage();
        repository.Setup(service => service.AddAsync(
                RecordingCompletedEventConsumerService.DlqStreamName,
                It.IsAny<Dictionary<string, string>>()))
            .ThrowsAsync(new InvalidOperationException("redis unavailable"));
        var handler = new Mock<IRecordingCompletedStreamMessageHandler>();
        handler.Setup(service => service.HandleAsync(
                It.IsAny<RedisStreamMessage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("invalid event"));
        var sut = CreateService(repository.Object, handler.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ConsumeBatchAsync("consumer-1", CancellationToken.None));

        repository.Verify(service => service.AcknowledgeAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    private static Mock<IRedisStreamRepository> CreateRepositoryWithOneNewMessage(
        string eventType = "meeting.recording_completed",
        string envelope = "{}")
    {
        var repository = new Mock<IRedisStreamRepository>();
        repository.Setup(service => service.ClaimStaleAsync(
                RecordingCompletedEventConsumerService.StreamName,
                RecordingCompletedEventConsumerService.GroupName,
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<int>()))
            .ReturnsAsync([]);
        repository.Setup(service => service.ReadGroupAsync(
                RecordingCompletedEventConsumerService.StreamName,
                RecordingCompletedEventConsumerService.GroupName,
                It.IsAny<string>(),
                ">",
                It.IsAny<int>()))
            .ReturnsAsync([
                new RedisStreamMessage
                {
                    Id = "1-0",
                    Values = new Dictionary<string, string>
                    {
                        ["event_type"] = eventType,
                        ["envelope"] = envelope
                    }
                }
            ]);
        repository.Setup(service => service.AddAsync(
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);
        repository.Setup(service => service.AcknowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        return repository;
    }

    private static RecordingCompletedEventConsumerService CreateService(
        IRedisStreamRepository repository,
        IRecordingCompletedStreamMessageHandler handler,
        ILogger<RecordingCompletedEventConsumerService>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        var provider = services.BuildServiceProvider();
        return new RecordingCompletedEventConsumerService(
            repository,
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger ?? NullLogger<RecordingCompletedEventConsumerService>.Instance);
    }
}
