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
        var processor = CreateProcessor();
        var sut = CreateSut(processor);
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

        var result = await sut.HandleAsync(Message(MeetingEventTypes.RecordingCompleted, envelope), CancellationToken.None);

        Assert.True(result.IsSuccess);
        processor.Verify(service => service.ProcessAsync(
            It.Is<EventEnvelope<MeetingRecordingCompletedEventPayload>>(value =>
                value.EventId == envelope.EventId &&
                value.Payload.EgressId == "EG_123"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// rec-loss: before this, recording_started fell into "unsupported event_type" and was
    /// dead-lettered, so no in-progress row was ever written.
    /// </summary>
    [Fact]
    public async Task HandleAsync_RoutesRecordingStarted_ToTheStartedOverload()
    {
        var processor = CreateProcessor();
        var sut = CreateSut(processor);
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingStarted,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingStartedEventPayload(Guid.NewGuid(), "EG_START", DateTime.UtcNow));

        var result = await sut.HandleAsync(Message(MeetingEventTypes.RecordingStarted, envelope), CancellationToken.None);

        Assert.True(result.IsSuccess);
        processor.Verify(service => service.ProcessAsync(
            It.Is<EventEnvelope<MeetingRecordingStartedEventPayload>>(value =>
                value.EventId == envelope.EventId && value.Payload.EgressId == "EG_START"),
            It.IsAny<CancellationToken>()), Times.Once);
        VerifyNoCompletedCall(processor);
    }

    [Fact]
    public async Task HandleAsync_RoutesRecordingFailed_ToTheFailedOverload()
    {
        var processor = CreateProcessor();
        var sut = CreateSut(processor);
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingFailed,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingFailedEventPayload(
                Guid.NewGuid(), "EG_FAIL", "The recording stopped unexpectedly.", "EGRESS_FAILED", "boom"));

        var result = await sut.HandleAsync(Message(MeetingEventTypes.RecordingFailed, envelope), CancellationToken.None);

        Assert.True(result.IsSuccess);
        processor.Verify(service => service.ProcessAsync(
            It.Is<EventEnvelope<MeetingRecordingFailedEventPayload>>(value =>
                value.EventId == envelope.EventId &&
                value.Payload.EgressId == "EG_FAIL" &&
                value.Payload.Reason == "The recording stopped unexpectedly."),
            It.IsAny<CancellationToken>()), Times.Once);
        VerifyNoCompletedCall(processor);
    }

    [Fact]
    public async Task HandleAsync_PassesAProcessorFailureThrough()
    {
        var processor = new Mock<IRecordingLifecycleEventProcessor>();
        processor.Setup(service => service.ProcessAsync(
                It.IsAny<EventEnvelope<MeetingRecordingFailedEventPayload>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<bool>("bad payload", ErrorCodes.ValidationError));
        var sut = CreateSut(processor);
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingFailed,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingFailedEventPayload(Guid.NewGuid(), "EG", "r", null, null));

        var result = await sut.HandleAsync(Message(MeetingEventTypes.RecordingFailed, envelope), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("bad payload", result.Error);
    }

    /// <summary>
    /// Unchanged behaviour: anything that is not a recording event is refused (and so ends in the
    /// DLQ), never silently acknowledged.
    /// </summary>
    [Fact]
    public async Task HandleAsync_RefusesANonRecordingEventType()
    {
        var processor = CreateProcessor();
        var sut = CreateSut(processor);
        var message = new RedisStreamMessage
        {
            Id = "1-0",
            Values = new Dictionary<string, string>
            {
                ["event_type"] = MeetingEventTypes.TrackPublished,
                ["envelope"] = "{}"
            }
        };

        var result = await sut.HandleAsync(message, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        VerifyNoCompletedCall(processor);
        processor.Verify(service => service.ProcessAsync(
            It.IsAny<EventEnvelope<MeetingRecordingStartedEventPayload>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        processor.Verify(service => service.ProcessAsync(
            It.IsAny<EventEnvelope<MeetingRecordingFailedEventPayload>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_RejectsMalformedEnvelope()
    {
        var processor = new Mock<IRecordingLifecycleEventProcessor>();
        var sut = CreateSut(processor);
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
        VerifyNoCompletedCall(processor);
    }

    private static Mock<IRecordingLifecycleEventProcessor> CreateProcessor()
    {
        var processor = new Mock<IRecordingLifecycleEventProcessor>();
        processor.Setup(service => service.ProcessAsync(
                It.IsAny<EventEnvelope<MeetingRecordingStartedEventPayload>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));
        processor.Setup(service => service.ProcessAsync(
                It.IsAny<EventEnvelope<MeetingRecordingCompletedEventPayload>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));
        processor.Setup(service => service.ProcessAsync(
                It.IsAny<EventEnvelope<MeetingRecordingFailedEventPayload>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));
        return processor;
    }

    private static RecordingCompletedStreamMessageHandler CreateSut(Mock<IRecordingLifecycleEventProcessor> processor) =>
        new(processor.Object, NullLogger<RecordingCompletedStreamMessageHandler>.Instance);

    private static RedisStreamMessage Message<TPayload>(string eventType, EventEnvelope<TPayload> envelope) =>
        new()
        {
            Id = "1-0",
            Values = new Dictionary<string, string>
            {
                ["event_type"] = eventType,
                ["envelope"] = JsonSerializer.Serialize(envelope)
            }
        };

    private static void VerifyNoCompletedCall(Mock<IRecordingLifecycleEventProcessor> processor) =>
        processor.Verify(service => service.ProcessAsync(
            It.IsAny<EventEnvelope<MeetingRecordingCompletedEventPayload>>(),
            It.IsAny<CancellationToken>()), Times.Never);
}
