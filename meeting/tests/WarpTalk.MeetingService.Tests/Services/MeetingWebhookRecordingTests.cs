using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.MeetingService.Tests.Services;

public sealed class MeetingWebhookRecordingTests
{
    private const string Secret = "test-webhook-secret-with-at-least-32-characters";

    [Fact]
    public async Task RoomFinished_PreservesActiveMeetingUntilIdleGracePeriodEnds()
    {
        var room = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = Guid.NewGuid(),
            ProviderRoomName = "room-123",
            Status = "IN_PROGRESS"
        };
        var unitOfWork = CreateUnitOfWork(room);
        var sut = CreateService(unitOfWork.Object, Mock.Of<IRedisService>());
        using var payload = JsonDocument.Parse(
            """
            {
              "event": "room_finished",
              "room": { "name": "room-123" }
            }
            """);

        var result = await sut.ProcessWebhookAsync(payload.RootElement);

        Assert.True(result.IsSuccess);
        Assert.Equal("IN_PROGRESS", room.Status);
        Assert.Null(room.EndedAt);
    }

    [Fact]
    public async Task EgressEnded_PublishesVersionedDurableRecordingEvent()
    {
        var translationRoomId = Guid.NewGuid();
        var room = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = "room-123",
            ActiveEgressId = "EG_123",
            Status = "Active"
        };
        var unitOfWork = CreateUnitOfWork(room);
        var redis = new Mock<IRedisService>();
        Dictionary<string, string>? publishedFields = null;
        redis.Setup(service => service.PublishStreamMessageAsync(
                "meeting:domain-events",
                It.IsAny<Dictionary<string, string>>()))
            .Callback<string, Dictionary<string, string>>((_, fields) => publishedFields = fields)
            .ReturnsAsync(Result.Success());
        var sut = CreateService(unitOfWork.Object, redis.Object);
        using var payload = JsonDocument.Parse(
            """
            {
              "event": "egress_ended",
              "egressInfo": {
                "egressId": "EG_123",
                "roomName": "room-123",
                "fileResults": [
                  {
                    "location": "s3://recordings/room-123.mp4",
                    "size": 4096
                  }
                ]
              }
            }
            """);

        var result = await sut.ProcessWebhookAsync(payload.RootElement);

        Assert.True(result.IsSuccess);
        Assert.Null(room.ActiveEgressId);
        Assert.NotNull(publishedFields);
        Assert.Equal(MeetingEventTypes.RecordingCompleted, publishedFields["event_type"]);
        var envelope = JsonSerializer.Deserialize<EventEnvelope<MeetingRecordingCompletedEventPayload>>(
            publishedFields["envelope"]);
        Assert.NotNull(envelope);
        Assert.Equal(DomainEventEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);
        Assert.Equal("meeting-service", envelope.Producer);
        Assert.Equal(translationRoomId, envelope.Payload.TranslationRoomId);
        Assert.Equal("EG_123", envelope.Payload.EgressId);
        Assert.Equal("s3://recordings/room-123.mp4", envelope.Payload.FileUrl);
        Assert.Equal(4096, envelope.Payload.FileSizeBytes);
        Assert.Equal("mp4", envelope.Payload.FileFormat);
    }

    [Fact]
    public async Task EgressEnded_ReturnsFailure_WhenDurablePublishFails()
    {
        var room = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = Guid.NewGuid(),
            ProviderRoomName = "room-123",
            ActiveEgressId = "EG_123",
            Status = "Active"
        };
        var unitOfWork = CreateUnitOfWork(room);
        var redis = new Mock<IRedisService>();
        redis.Setup(service => service.PublishStreamMessageAsync(
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(Result.Failure("redis unavailable", "REDIS_ERROR"));
        var sut = CreateService(unitOfWork.Object, redis.Object);
        using var payload = JsonDocument.Parse(
            """
            {
              "event": "egress_ended",
              "egressInfo": {
                "egressId": "EG_123",
                "fileResults": [
                  { "location": "s3://recordings/room-123.mp4" }
                ]
              }
            }
            """);

        var result = await sut.ProcessWebhookAsync(payload.RootElement);

        Assert.False(result.IsSuccess);
        unitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Mock<IUnitOfWork> CreateUnitOfWork(MeetingRoom room)
    {
        var roomRepository = new Mock<IMeetingRoomRepository>();
        roomRepository.Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.MeetingRoomRepository).Returns(roomRepository.Object);
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        return unitOfWork;
    }

    private static MeetingWebhookService CreateService(IUnitOfWork unitOfWork, IRedisService redis)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:ApiSecret"] = Secret
            })
            .Build();
        return new MeetingWebhookService(
            unitOfWork,
            redis,
            configuration,
            NullLogger<MeetingWebhookService>.Instance);
    }
}
