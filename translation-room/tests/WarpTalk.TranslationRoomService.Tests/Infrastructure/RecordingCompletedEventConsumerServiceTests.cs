using Microsoft.Extensions.DependencyInjection;
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

    private static Mock<IRedisStreamRepository> CreateRepositoryWithOneNewMessage()
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
                        ["event_type"] = "meeting.recording_completed",
                        ["envelope"] = "{}"
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
        IRecordingCompletedStreamMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        var provider = services.BuildServiceProvider();
        return new RecordingCompletedEventConsumerService(
            repository,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RecordingCompletedEventConsumerService>.Instance);
    }
}
