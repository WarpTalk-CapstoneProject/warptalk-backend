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

/// <summary>
/// WT-923. The ingress bot used to be summoned only by meeting.track_published, so a person who
/// joined muted and unmuted to speak had their first sentence said while the bot was still
/// dialling LiveKit. participant_joined now summons it as soon as a person is in the room.
/// </summary>
public sealed class MeetingWebhookParticipantJoinedTests
{
    private const string Secret = "test-webhook-secret-with-at-least-32-characters";

    [Fact]
    public async Task HumanJoin_PublishesVersionedParticipantJoinedEvent()
    {
        var (redis, published) = CaptureRedis();
        var sut = CreateService(CreateUnitOfWork(room: null).Object, redis.Object);

        var result = await sut.ProcessWebhookAsync(Join("room-123", "user-42"));

        Assert.True(result.IsSuccess);
        var (channel, envelope) = Assert.Single(published);
        Assert.Equal(MeetingEventTypes.ParticipantJoined, channel);
        Assert.Equal("meeting.participant_joined", envelope.EventType);
        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Equal("meeting-service", envelope.Producer);
        Assert.Equal("room-123", envelope.Payload.RoomName);
        Assert.Equal("user-42", envelope.Payload.ParticipantIdentity);
    }

    [Theory]
    [InlineData("AIBot_room-123")]
    [InlineData("ai-interpreter-vi")]
    public async Task BotJoin_DoesNotSummonTheBot(string identity)
    {
        var (redis, published) = CaptureRedis();
        var sut = CreateService(CreateUnitOfWork(room: null).Object, redis.Object);

        var result = await sut.ProcessWebhookAsync(Join("room-123", identity));

        Assert.True(result.IsSuccess);
        Assert.Empty(published);
    }

    [Fact]
    public async Task PublishFailure_StillRecordsTheJoin()
    {
        // Best effort: track_published still summons the bot, so a Redis hiccup here must cost
        // the warm-up and nothing else — not the participant's JoinedAt, and not a 500 that makes
        // LiveKit retry the webhook.
        var room = new MeetingRoom { Id = Guid.NewGuid(), ProviderRoomName = "room-123", Status = "IN_PROGRESS" };
        var participant = new RtcStreamParticipant
        {
            Id = Guid.NewGuid(),
            MeetingRoomId = room.Id,
            ProviderIdentity = "user-42",
            LeftAt = DateTime.UtcNow.AddMinutes(-1),
        };
        var redis = new Mock<IRedisService>();
        redis.Setup(service => service.PublishEventAsync(
                MeetingEventTypes.ParticipantJoined,
                It.IsAny<EventEnvelope<MeetingParticipantJoinedEventPayload>>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));
        var sut = CreateService(CreateUnitOfWork(room, participant).Object, redis.Object);

        var result = await sut.ProcessWebhookAsync(Join("room-123", "user-42"));

        Assert.True(result.IsSuccess);
        Assert.Null(participant.LeftAt);
        Assert.NotNull(participant.JoinedAt);
    }

    private static JsonElement Join(string roomName, string identity) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            @event = "participant_joined",
            room = new { name = roomName },
            participant = new { identity },
        })).RootElement;

    private static (Mock<IRedisService> Redis, List<(string Channel, EventEnvelope<MeetingParticipantJoinedEventPayload> Envelope)> Published)
        CaptureRedis()
    {
        var published = new List<(string, EventEnvelope<MeetingParticipantJoinedEventPayload>)>();
        var redis = new Mock<IRedisService>();
        redis.Setup(service => service.PublishEventAsync(
                It.IsAny<string>(),
                It.IsAny<EventEnvelope<MeetingParticipantJoinedEventPayload>>()))
            .Callback<string, EventEnvelope<MeetingParticipantJoinedEventPayload>>(
                (channel, envelope) => published.Add((channel, envelope)))
            .ReturnsAsync(Result.Success());
        return (redis, published);
    }

    private static Mock<IUnitOfWork> CreateUnitOfWork(MeetingRoom? room, RtcStreamParticipant? participant = null)
    {
        var roomRepository = new Mock<IMeetingRoomRepository>();
        roomRepository.Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                room is not null && predicate.Compile()(room) ? room : null);

        var participantRepository = new Mock<IRtcStreamParticipantRepository>();
        participantRepository.Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<RtcStreamParticipant, bool>> predicate, string _, CancellationToken _) =>
                participant is not null && predicate.Compile()(participant) ? participant : null);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.MeetingRoomRepository).Returns(roomRepository.Object);
        unitOfWork.SetupGet(work => work.RtcStreamParticipantRepository).Returns(participantRepository.Object);
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        return unitOfWork;
    }

    private static MeetingWebhookService CreateService(IUnitOfWork unitOfWork, IRedisService redis)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LiveKit:ApiSecret"] = Secret })
            .Build();
        return new MeetingWebhookService(
            unitOfWork,
            redis,
            new EgressCompletion(unitOfWork, redis, NullLogger<EgressCompletion>.Instance),
            configuration,
            NullLogger<MeetingWebhookService>.Instance);
    }
}
