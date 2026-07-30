using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Redis;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

public sealed class RecordingCompletedStreamMessageHandlerTests
{
    [Fact]
    public async Task HandleAsync_DeserializesEnvelope_AndProcessesIt()
    {
        var processor = new Mock<IRecordingCompletedEventProcessor>();
        processor.Setup(service => service.ProcessAsync(
                It.IsAny<EventEnvelope<MeetingRecordingCompletedEventPayload>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));
        var sut = new RecordingCompletedStreamMessageHandler(
            processor.Object,
            NullLogger<RecordingCompletedStreamMessageHandler>.Instance);
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingCompleted,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingCompletedEventPayload(
                Guid.NewGuid(),
                "EG_123",
                "s3://recordings/room.mp4",
                "mp4",
                4096,
                true,
                true));
        var message = new RedisStreamMessage
        {
            Id = "1-0",
            Values = new Dictionary<string, string>
            {
                ["event_type"] = MeetingEventTypes.RecordingCompleted,
                ["envelope"] = JsonSerializer.Serialize(envelope)
            }
        };

        var result = await sut.HandleAsync(message, CancellationToken.None);

        Assert.True(result.IsSuccess);
        processor.Verify(service => service.ProcessAsync(
            It.Is<EventEnvelope<MeetingRecordingCompletedEventPayload>>(value =>
                value.EventId == envelope.EventId &&
                value.Payload.EgressId == "EG_123"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_RejectsMalformedEnvelope()
    {
        var processor = new Mock<IRecordingCompletedEventProcessor>();
        var sut = new RecordingCompletedStreamMessageHandler(
            processor.Object,
            NullLogger<RecordingCompletedStreamMessageHandler>.Instance);
        var message = new RedisStreamMessage
        {
            Id = "1-0",
            Values = new Dictionary<string, string>
            {
                ["event_type"] = MeetingEventTypes.RecordingCompleted,
                ["envelope"] = "{not-json"
            }
        };

        var result = await sut.HandleAsync(message, CancellationToken.None);

        Assert.False(result.IsSuccess);
        processor.Verify(service => service.ProcessAsync(
            It.IsAny<EventEnvelope<MeetingRecordingCompletedEventPayload>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
