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

    /// <summary>
    /// WT-473. LiveKit reports the egress start as a UNIX timestamp in NANOSECONDS — a proto int64.
    ///
    /// Reading it as seconds puts the recording in the year 56000; reading it as milliseconds puts
    /// it 50,000 years early. Both render as a plausible-looking date rather than an error, which is
    /// exactly why this needs a test with a literal expected instant rather than a round-trip.
    /// </summary>
    [Fact]
    public async Task EgressEnded_PublishesTheRecordingStartAsUtc()
    {
        var room = NewRecordingRoom();
        var unitOfWork = CreateUnitOfWork(room);
        var (redis, published) = CaptureRedis();
        var sut = CreateService(unitOfWork.Object, redis.Object);

        // 2026-08-17T10:24:00Z expressed in nanoseconds.
        using var payload = JsonDocument.Parse(
            """
            {
              "event": "egress_ended",
              "egressInfo": {
                "egressId": "EG_123",
                "roomName": "room-123",
                "startedAt": 1786962240000000000,
                "fileResults": [ { "location": "s3://recordings/room-123.mp4", "size": 4096 } ]
              }
            }
            """);

        var result = await sut.ProcessWebhookAsync(payload.RootElement);

        Assert.True(result.IsSuccess);
        var envelope = ReadEnvelope(published);
        Assert.Equal(
            new DateTime(2026, 8, 17, 10, 24, 0, DateTimeKind.Utc),
            envelope.Payload.StartedAt);
    }

    /// <summary>
    /// snake_case as well as camelCase, for the reason the rest of this handler accepts both:
    /// LiveKit's Twirp JSON emits camelCase, the proto field names are snake_case, and some
    /// deployments send those. Reading only one spelling is a silently un-seekable recording.
    /// </summary>
    [Fact]
    public async Task EgressEnded_ReadsTheRecordingStartFromSnakeCase()
    {
        var room = NewRecordingRoom();
        var unitOfWork = CreateUnitOfWork(room);
        var (redis, published) = CaptureRedis();
        var sut = CreateService(unitOfWork.Object, redis.Object);

        using var payload = JsonDocument.Parse(
            """
            {
              "event": "egress_ended",
              "egressInfo": {
                "egress_id": "EG_123",
                "room_name": "room-123",
                "started_at": 1786962240000000000,
                "fileResults": [ { "location": "s3://recordings/room-123.mp4" } ]
              }
            }
            """);

        var result = await sut.ProcessWebhookAsync(payload.RootElement);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new DateTime(2026, 8, 17, 10, 24, 0, DateTimeKind.Utc),
            ReadEnvelope(published).Payload.StartedAt);
    }

    /// <summary>
    /// JSON cannot hold an int64 losslessly, so some emitters quote large numbers. A quoted start
    /// must not be read as "absent".
    /// </summary>
    [Fact]
    public async Task EgressEnded_AcceptsAQuotedRecordingStart()
    {
        var room = NewRecordingRoom();
        var unitOfWork = CreateUnitOfWork(room);
        var (redis, published) = CaptureRedis();
        var sut = CreateService(unitOfWork.Object, redis.Object);

        using var payload = JsonDocument.Parse(
            """
            {
              "event": "egress_ended",
              "egressInfo": {
                "egressId": "EG_123",
                "roomName": "room-123",
                "startedAt": "1786962240000000000",
                "fileResults": [ { "location": "s3://recordings/room-123.mp4" } ]
              }
            }
            """);

        var result = await sut.ProcessWebhookAsync(payload.RootElement);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new DateTime(2026, 8, 17, 10, 24, 0, DateTimeKind.Utc),
            ReadEnvelope(published).Payload.StartedAt);
    }

    /// <summary>
    /// Absent and zero both mean "not known". LiveKit reports 0 for an egress that never started,
    /// and storing 1970-01-01 would be indistinguishable downstream from a real recording made then
    /// — a seek that is confidently wrong rather than declared un-seekable.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\"startedAt\": 0,")]
    public async Task EgressEnded_LeavesTheRecordingStartNull_WhenItIsAbsentOrZero(string startedAtLine)
    {
        var room = NewRecordingRoom();
        var unitOfWork = CreateUnitOfWork(room);
        var (redis, published) = CaptureRedis();
        var sut = CreateService(unitOfWork.Object, redis.Object);

        using var payload = JsonDocument.Parse(
            $$"""
            {
              "event": "egress_ended",
              "egressInfo": {
                "egressId": "EG_123",
                "roomName": "room-123",
                {{startedAtLine}}
                "fileResults": [ { "location": "s3://recordings/room-123.mp4" } ]
              }
            }
            """);

        var result = await sut.ProcessWebhookAsync(payload.RootElement);

        Assert.True(result.IsSuccess);
        Assert.Null(ReadEnvelope(published).Payload.StartedAt);
    }

    private static MeetingRoom NewRecordingRoom() => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = Guid.NewGuid(),
        ProviderRoomName = "room-123",
        ActiveEgressId = "EG_123",
        Status = "Active",
    };

    private static (Mock<IRedisService> Redis, Func<Dictionary<string, string>?> Published) CaptureRedis()
    {
        Dictionary<string, string>? fields = null;
        var redis = new Mock<IRedisService>();
        redis.Setup(service => service.PublishStreamMessageAsync(
                "meeting:domain-events",
                It.IsAny<Dictionary<string, string>>()))
            .Callback<string, Dictionary<string, string>>((_, published) => fields = published)
            .ReturnsAsync(Result.Success());
        return (redis, () => fields);
    }

    private static EventEnvelope<MeetingRecordingCompletedEventPayload> ReadEnvelope(
        Func<Dictionary<string, string>?> published)
    {
        var fields = published();
        Assert.NotNull(fields);
        var envelope = JsonSerializer.Deserialize<EventEnvelope<MeetingRecordingCompletedEventPayload>>(
            fields!["envelope"]);
        Assert.NotNull(envelope);
        return envelope!;
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
        // The REAL EgressCompletion, not a mock. The egress assertions in this file are about what
        // finishing a recording actually does — clearing ActiveEgressId, publishing the versioned
        // envelope — and that work moved into EgressCompletion when the reconciliation sweep
        // became its second caller. Mocking it here would leave the tests passing while testing
        // nothing but a delegation.
        return new MeetingWebhookService(
            unitOfWork,
            redis,
            new EgressCompletion(unitOfWork, redis),
            configuration,
            NullLogger<MeetingWebhookService>.Instance);
    }
}
