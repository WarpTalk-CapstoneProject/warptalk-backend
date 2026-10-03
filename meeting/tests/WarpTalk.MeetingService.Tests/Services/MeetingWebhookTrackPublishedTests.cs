using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Enums;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// 3 Oct 2026. track_published read <c>track.kind</c>, a field LiveKit never sends, and threw on
/// every webhook since May: zero rows in meeting.meeting_tracks on production, and a 500 for
/// LiveKit to retry each time. The payloads below are shaped the way LiveKit sends them: TrackInfo
/// carries <c>type</c>, and protobuf JSON omits it for AUDIO because AUDIO is the enum's 0.
/// </summary>
public sealed class MeetingWebhookTrackPublishedTests
{
    private const string Secret = "test-webhook-secret-with-at-least-32-characters";

    [Fact]
    public async Task AnAudioTrackWithNoTypeFieldIsRecordedAndSummonsTheBot()
    {
        var (room, participant) = RoomWith("room-123", "user-42");
        var (redis, published) = CaptureRedis();
        var tracks = new List<MeetingTrack>();
        var sut = CreateService(CreateUnitOfWork(room, participant, tracks).Object, redis.Object);

        var result = await sut.ProcessWebhookAsync(TrackPublished("room-123", "user-42", "TR_AUDIO1", type: null));

        Assert.True(result.IsSuccess);
        var track = Assert.Single(tracks);
        Assert.Equal("TR_AUDIO1", track.ProviderTrackId);
        Assert.Equal(participant.Id, track.RtcStreamParticipantId);
        Assert.Equal(MediaType.Audio.ToString(), track.MediaType);
        var (channel, envelope) = Assert.Single(published);
        Assert.Equal(MeetingEventTypes.TrackPublished, channel);
        Assert.Equal("TR_AUDIO1", envelope.Payload.TrackId);
        Assert.Equal("room-123", envelope.Payload.RoomName);
    }

    [Fact]
    public async Task AVideoTrackIsRecordedButDoesNotSummonTheBot()
    {
        var (room, participant) = RoomWith("room-123", "user-42");
        var (redis, published) = CaptureRedis();
        var tracks = new List<MeetingTrack>();
        var sut = CreateService(CreateUnitOfWork(room, participant, tracks).Object, redis.Object);

        var result = await sut.ProcessWebhookAsync(TrackPublished("room-123", "user-42", "TR_VIDEO1", type: "VIDEO"));

        Assert.True(result.IsSuccess);
        Assert.Equal(MediaType.Video.ToString(), Assert.Single(tracks).MediaType);
        Assert.Empty(published);
    }

    [Fact]
    public async Task ATrackIsFiledUnderThisRoomsParticipantNotAnotherMeetings()
    {
        // The same identity has a row in every meeting the user has joined. Only this room's counts.
        var (room, _) = RoomWith("room-123", "user-42");
        var otherMeetingsRow = new RtcStreamParticipant
        {
            Id = Guid.NewGuid(), MeetingRoomId = Guid.NewGuid(), ProviderIdentity = "user-42",
        };
        var (redis, published) = CaptureRedis();
        var tracks = new List<MeetingTrack>();
        var sut = CreateService(CreateUnitOfWork(room, otherMeetingsRow, tracks).Object, redis.Object);

        var result = await sut.ProcessWebhookAsync(TrackPublished("room-123", "user-42", "TR_AUDIO1", type: null));

        Assert.True(result.IsSuccess);
        Assert.Empty(tracks);
        Assert.Empty(published);
    }

    [Theory]
    [InlineData(null, "audio")]
    [InlineData("AUDIO", "audio")]
    [InlineData("VIDEO", "video")]
    [InlineData("DATA", "data")]
    public void TrackTypeReadsLiveKitsField(string? type, string expected)
    {
        var track = JsonDocument.Parse(type is null ? "{\"sid\":\"TR_1\"}" : $"{{\"sid\":\"TR_1\",\"type\":\"{type}\"}}").RootElement;
        Assert.Equal(expected, MeetingWebhookService.TrackType(track));
    }

    [Fact]
    public void TrackTypeAcceptsTheNumericEnumToo()
    {
        Assert.Equal("video", MeetingWebhookService.TrackType(JsonDocument.Parse("{\"type\":1}").RootElement));
        Assert.Equal("audio", MeetingWebhookService.TrackType(JsonDocument.Parse("{\"type\":0}").RootElement));
    }

    private static (MeetingRoom Room, RtcStreamParticipant Participant) RoomWith(string roomName, string identity)
    {
        var room = new MeetingRoom { Id = Guid.NewGuid(), ProviderRoomName = roomName, Status = "IN_PROGRESS" };
        var participant = new RtcStreamParticipant { Id = Guid.NewGuid(), MeetingRoomId = room.Id, ProviderIdentity = identity };
        return (room, participant);
    }

    private static JsonElement TrackPublished(string roomName, string identity, string trackSid, string? type)
    {
        var track = new Dictionary<string, object?> { ["sid"] = trackSid, ["source"] = "MICROPHONE" };
        if (type is not null) track["type"] = type;
        return JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["event"] = "track_published",
            ["room"] = new { name = roomName },
            ["participant"] = new { identity },
            ["track"] = track,
        })).RootElement;
    }

    private static (Mock<IRedisService> Redis, List<(string Channel, EventEnvelope<MeetingTrackPublishedEventPayload> Envelope)> Published)
        CaptureRedis()
    {
        var published = new List<(string, EventEnvelope<MeetingTrackPublishedEventPayload>)>();
        var redis = new Mock<IRedisService>();
        redis.Setup(service => service.PublishEventAsync(
                It.IsAny<string>(),
                It.IsAny<EventEnvelope<MeetingTrackPublishedEventPayload>>()))
            .Callback<string, EventEnvelope<MeetingTrackPublishedEventPayload>>(
                (channel, envelope) => published.Add((channel, envelope)))
            .ReturnsAsync(Result.Success());
        return (redis, published);
    }

    private static Mock<IUnitOfWork> CreateUnitOfWork(
        MeetingRoom? room, RtcStreamParticipant? participant, List<MeetingTrack> tracks)
    {
        var roomRepository = new Mock<IMeetingRoomRepository>();
        roomRepository.Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                room is not null && predicate.Compile()(room) ? room : null);

        var participantRepository = new Mock<IRtcStreamParticipantRepository>();
        participantRepository.Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<RtcStreamParticipant, bool>> predicate, string _, CancellationToken _) =>
                participant is not null && predicate.Compile()(participant) ? participant : null);

        var trackRepository = new Mock<IMeetingTrackRepository>();
        trackRepository.Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingTrack, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingTrack, bool>> predicate, string _, CancellationToken _) =>
                tracks.FirstOrDefault(predicate.Compile()));
        trackRepository.Setup(repository => repository.AddAsync(It.IsAny<MeetingTrack>(), It.IsAny<CancellationToken>()))
            .Callback<MeetingTrack, CancellationToken>((track, _) => tracks.Add(track))
            .Returns(Task.CompletedTask);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.MeetingRoomRepository).Returns(roomRepository.Object);
        unitOfWork.SetupGet(work => work.RtcStreamParticipantRepository).Returns(participantRepository.Object);
        unitOfWork.SetupGet(work => work.MeetingTrackRepository).Returns(trackRepository.Object);
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
