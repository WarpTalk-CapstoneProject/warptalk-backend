using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

public class MeetingRoomServiceTests
{
    private readonly Mock<ILiveKitTokenService> _tokenServiceMock = new();
    private readonly Mock<ITranslationRoomGrpcService> _grpcServiceMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IRedisService> _redisServiceMock = new();
    private readonly Mock<ILiveKitEgressService> _egressServiceMock = new();
    private readonly Mock<ILiveKitRoomAdminService> _roomAdminServiceMock = new();
    private readonly MeetingRoomService _sut;

    public MeetingRoomServiceTests()
    {
        _redisServiceMock
            .Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ReturnsAsync(Result.Success());
        _redisServiceMock
            .Setup(r => r.PublishStreamMessageAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(Result.Success());
        _roomAdminServiceMock
            .Setup(r => r.RemoveParticipantAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));
        _roomAdminServiceMock
            .Setup(r => r.DeleteRoomAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));

        _sut = new MeetingRoomService(
            _tokenServiceMock.Object,
            _grpcServiceMock.Object,
            _unitOfWorkMock.Object,
            _redisServiceMock.Object,
            _egressServiceMock.Object,
            _roomAdminServiceMock.Object,
            Mock.Of<ILogger<MeetingRoomService>>());
    }

    private static Mock<IMeetingRoomRepository> SetupMeetingRoomRepository(Mock<IUnitOfWork> unitOfWorkMock, MeetingRoom? meetingRoom)
    {
        var roomRepoMock = new Mock<IMeetingRoomRepository>();
        roomRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(meetingRoom);
        // Emulates the conditional UPDATE: clears only when the expected host still holds the room.
        roomRepoMock
            .Setup(r => r.ClearActiveHostIfAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid translationRoomId, Guid expectedHostId, CancellationToken _) =>
            {
                if (meetingRoom is null
                    || meetingRoom.TranslationRoomId != translationRoomId
                    || meetingRoom.ActiveHostId != expectedHostId)
                {
                    return 0;
                }

                meetingRoom.ActiveHostId = null;
                return 1;
            });
        unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(roomRepoMock.Object);
        return roomRepoMock;
    }

    /// <summary>
    /// The single participant row a membership lookup returns, or null for "not in the room".
    /// </summary>
    private static Mock<IRtcStreamParticipantRepository> SetupMeetingParticipant(
        Mock<IUnitOfWork> unitOfWorkMock,
        RtcStreamParticipant? participant)
    {
        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);
        return participantRepoMock;
    }

    [Fact]
    public async Task SetLockAsync_LocksRoom_WhenCallerIsActiveHost()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = hostId, ProviderRoomName = translationRoomId.ToString() };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));

        var result = await _sut.SetLockAsync(translationRoomId, hostId, true);

        Assert.True(result.IsSuccess);
        Assert.True(meetingRoom.IsLocked);
        roomRepoMock.Verify(r => r.Update(meetingRoom), Times.Once);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<object>(payload => HasProperty(payload, "Command", "RoomLockChanged") && HasProperty(payload, "RoomId", translationRoomId.ToString()))),
            Times.Once);
    }

    [Fact]
    public async Task SetLockAsync_ReturnsForbidden_WhenCallerIsNotHost()
    {
        var translationRoomId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = Guid.NewGuid(), ProviderRoomName = translationRoomId.ToString() };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));

        var result = await _sut.SetLockAsync(translationRoomId, Guid.NewGuid(), true);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.False(meetingRoom.IsLocked);
    }

    [Fact]
    public async Task JoinMeetingAsync_RejectsNewJoiner_WhenRoomIsLocked()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var meetingRoomId = Guid.NewGuid();

        var roomDetails = new WarpTalk.Shared.Protos.GetTranslationRoomResponse
        {
            HostId = hostId.ToString(),
            Status = "IN_PROGRESS",
            WorkspaceId = Guid.NewGuid().ToString()
        };
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(roomDetails));

        var meetingRoom = new MeetingRoom
        {
            Id = meetingRoomId,
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "IN_PROGRESS",
            IsLocked = true
        };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcStreamParticipant?)null);
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        var participantsCacheKey = $"meeting:participants:{translationRoomId}";
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetParticipantsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse()));

        var invitationRepoMock = new Mock<IRtcSessionRevocationRepository>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcSessionRevocation?)null);
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository).Returns(invitationRepoMock.Object);

        var result = await _sut.JoinMeetingAsync(translationRoomId, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal("Room is locked.", result.Error);
        _unitOfWorkMock.Verify(u => u.RollbackTransactionAsync(), Times.Once);
    }

    [Fact]
    public async Task JoinMeetingAsync_IssuesToken_WhenWaitingRoomParticipantWasAdmitted()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "WAITING"
        };

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse
                {
                    HostId = hostId.ToString(),
                    Status = "WAITING",
                    WorkspaceId = Guid.NewGuid().ToString(),
                    // WT-428: the lobby now keys on this setting, not on room status.
                    RequiresApproval = true
                }));
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var meetingParticipant = new RtcStreamParticipant
        {
            Id = Guid.NewGuid(),
            MeetingRoomId = meetingRoom.Id,
            UserId = userId,
            ProviderIdentity = userId.ToString(),
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        };
        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(meetingParticipant);
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository)
            .Returns(participantRepoMock.Object);

        var invitationRepoMock = new Mock<IRtcSessionRevocationRepository>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcSessionRevocation?)null);
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository)
            .Returns(invitationRepoMock.Object);

        var translationParticipants = new WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse();
        translationParticipants.Participants.Add(new WarpTalk.Shared.Protos.Participant
        {
            Id = userId.ToString(),
            DisplayName = "Admitted participant",
            IsActive = true
        });
        _grpcServiceMock
            .Setup(g => g.GetParticipantsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(translationParticipants));
        _tokenServiceMock
            .Setup(service => service.GenerateToken(
                translationRoomId.ToString(),
                userId.ToString(),
                "Admitted participant",
                true,
                true))
            .Returns(Result.Success("livekit-token"));

        var result = await _sut.JoinMeetingAsync(
            translationRoomId,
            userId,
            "Admitted participant");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsWaitingRoom);
        Assert.Equal("livekit-token", result.Value.Token);
    }

    [Fact]
    public async Task SetRecordingAsync_StartsEgress_AndPersistsEgressId_WhenCallerIsHost()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = hostId, ProviderRoomName = "room-1" };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));

        _egressServiceMock
            .Setup(e => e.StartRoomCompositeEgressAsync("room-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("egress-123"));

        var result = await _sut.SetRecordingAsync(translationRoomId, hostId, "start");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Recording);
        Assert.Equal("egress-123", result.Value.EgressId);
        Assert.Equal("egress-123", meetingRoom.ActiveEgressId);
        roomRepoMock.Verify(r => r.Update(meetingRoom), Times.Once);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<object>(payload => HasProperty(payload, "Command", "RecordingStateChanged"))),
            Times.Once);
    }

    /// <summary>
    /// rec-loss: a started recording announces itself on meeting:domain-events, so its ending
    /// (Completed or Failed) resolves something instead of being the only trace it ever left.
    /// </summary>
    [Fact]
    public async Task SetRecordingAsync_PublishesRecordingStarted_WhenTheEgressStarts()
    {
        var translationRoomId = SetupStartableRecording(Result.Success("egress-123"), out var hostId);
        var published = CaptureDomainEvents();

        var before = DateTime.UtcNow;
        var result = await _sut.SetRecordingAsync(translationRoomId, hostId, "start");

        Assert.True(result.IsSuccess);
        var fields = Assert.Single(published);
        Assert.Equal(MeetingEventTypes.RecordingStarted, fields["event_type"]);
        Assert.Equal(DomainEventEnvelope.CurrentSchemaVersion.ToString(), fields["schema_version"]);
        var envelope = JsonSerializer.Deserialize<EventEnvelope<MeetingRecordingStartedEventPayload>>(fields["envelope"])!;
        Assert.Equal(fields["event_id"], envelope.EventId.ToString());
        Assert.Equal("meeting-service", envelope.Producer);
        Assert.Equal(translationRoomId, envelope.Payload.TranslationRoomId);
        Assert.Equal("egress-123", envelope.Payload.EgressId);
        Assert.InRange(envelope.Payload.StartedAt, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task SetRecordingAsync_PublishesNothing_WhenTheEgressDoesNotStart()
    {
        var translationRoomId = SetupStartableRecording(
            Result.Failure<string>("quota", ErrorCodes.InternalServerError),
            out var hostId);
        var published = CaptureDomainEvents();

        var result = await _sut.SetRecordingAsync(translationRoomId, hostId, "start");

        Assert.False(result.IsSuccess);
        Assert.Empty(published);
    }

    /// <summary>
    /// The egress is already running when the Started publish fails, and the webhook or sweep will
    /// still finish it. Failing the request would tell the host a running recording did not start.
    /// </summary>
    [Fact]
    public async Task SetRecordingAsync_StillSucceeds_WhenRecordingStartedCannotBePublished()
    {
        var translationRoomId = SetupStartableRecording(Result.Success("egress-123"), out var hostId);
        _redisServiceMock
            .Setup(r => r.PublishStreamMessageAsync("meeting:domain-events", It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(Result.Failure("redis unavailable", "REDIS_ERROR"));

        var result = await _sut.SetRecordingAsync(translationRoomId, hostId, "start");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Recording);
        Assert.Equal("egress-123", result.Value.EgressId);
    }

    private Guid SetupStartableRecording(Result<string> startResult, out Guid hostId)
    {
        var translationRoomId = Guid.NewGuid();
        hostId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = hostId, ProviderRoomName = "room-1" };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));
        _egressServiceMock
            .Setup(e => e.StartRoomCompositeEgressAsync("room-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(startResult);
        return translationRoomId;
    }

    private List<Dictionary<string, string>> CaptureDomainEvents()
    {
        var published = new List<Dictionary<string, string>>();
        _redisServiceMock
            .Setup(r => r.PublishStreamMessageAsync("meeting:domain-events", It.IsAny<Dictionary<string, string>>()))
            .Callback<string, Dictionary<string, string>>((_, fields) => published.Add(fields))
            .ReturnsAsync(Result.Success());
        return published;
    }

    /// <summary>
    /// WT-644. Stop reports the recording as stopped — capture really has ended — but it must NOT
    /// erase <c>ActiveEgressId</c>, which is the only durable record that a recording is still
    /// being finalised and owes us an artifact.
    /// </summary>
    [Fact]
    public async Task SetRecordingAsync_StopsEgress_ButKeepsTheEgressIdUntilTheRecordingLands()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = hostId, ProviderRoomName = "room-1", ActiveEgressId = "egress-123" };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));

        _egressServiceMock
            .Setup(e => e.StopEgressAsync("egress-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));

        var result = await _sut.SetRecordingAsync(translationRoomId, hostId, "stop");

        Assert.True(result.IsSuccess);
        // What the person who pressed the button is told, and what the row remembers, are
        // deliberately different things.
        Assert.False(result.Value!.Recording);
        Assert.Null(result.Value.EgressId);
        Assert.Equal("egress-123", meetingRoom.ActiveEgressId);
        roomRepoMock.Verify(r => r.Update(meetingRoom), Times.Once);
    }

    /// <summary>
    /// WT-644 — THE WHOLE POINT OF KEEPING THE ID, END TO END.
    ///
    /// The user-visible bug was a finished meeting whose record page showed no recording and
    /// Artifacts (0). The webhook half of that is fixed elsewhere (EgressCompletion matches the
    /// room by name when Stop got there first). This is the other half: a webhook that NEVER
    /// ARRIVES — the failure WT-371 #8 built the reconciliation sweep for, when the LiveKit
    /// project had no webhook configured at all.
    ///
    /// EgressReconciliationService selects rooms with <c>ActiveEgressId != null</c>. While Stop
    /// nulled that column, the sweep's own predicate excluded every recording that had been
    /// stopped through the UI — which is to say, all of them — so the fallback could not fire on
    /// the only path that ever needed it. This runs the REAL stop and then the REAL sweep over a
    /// repository that honours the predicate, and fails on the production code as it stood.
    /// </summary>
    [Fact]
    public async Task AStoppedRecordingIsStillCompletedByTheSweep_WhenTheWebhookNeverArrives()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ActiveHostId = hostId,
            ProviderRoomName = "room-1",
            ActiveEgressId = "egress-123",
            Status = "IN_PROGRESS",
            UpdatedAt = DateTime.UtcNow.AddMinutes(-20)
        };

        // Compiled, not "return the room for anything". A mock that answers every predicate with
        // the same row cannot tell a working sweep from a broken one — the sweep's bug WAS its
        // predicate.
        var rooms = new[] { meetingRoom };
        var roomRepoMock = new Mock<IMeetingRoomRepository>();
        roomRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                rooms.FirstOrDefault(predicate.Compile()));
        roomRepoMock
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                rooms.Where(predicate.Compile()).ToList());
        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(roomRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));
        _egressServiceMock
            .Setup(e => e.StopEgressAsync("egress-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));

        var stop = await _sut.SetRecordingAsync(translationRoomId, hostId, "stop");
        Assert.True(stop.IsSuccess);

        // LiveKit finished the upload. No webhook was ever delivered, so the sweep is the only
        // thing left that can turn it into an artifact.
        using var finished = JsonDocument.Parse(
            """
            {
              "egressId": "egress-123",
              "status": "EGRESS_COMPLETE",
              "fileResults": [ { "location": "s3://recordings/room-1.mp4", "size": 2048 } ]
            }
            """);
        _egressServiceMock
            .Setup(e => e.GetEgressAsync("egress-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<JsonElement?>(finished.RootElement.Clone()));

        var sweep = new EgressReconciliationService(
            _unitOfWorkMock.Object,
            _egressServiceMock.Object,
            new EgressCompletion(_unitOfWorkMock.Object, _redisServiceMock.Object, NullLogger<EgressCompletion>.Instance),
            Mock.Of<ILogger<EgressReconciliationService>>());

        var result = await sweep.ReconcileAsync(DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        // The recording now exists as far as the rest of the system is concerned, and the room has
        // stopped holding the egress.
        Assert.Null(meetingRoom.ActiveEgressId);
        _redisServiceMock.Verify(
            r => r.PublishStreamMessageAsync("meeting:domain-events", It.IsAny<Dictionary<string, string>>()),
            Times.Once);
    }

    // Recording is host-only (owner decision, 2026-10-02): the room's booker or its active host.
    // It had been widened to every participant; the two tests below pin that it no longer is —
    // neither for a stranger holding the room id nor for someone who is really in the room.

    [Fact]
    public async Task SetRecordingAsync_ReturnsForbidden_WhenCallerIsNotInTheMeeting()
    {
        var translationRoomId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = Guid.NewGuid(), ProviderRoomName = "room-1" };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);
        SetupMeetingParticipant(_unitOfWorkMock, null);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));

        var result = await _sut.SetRecordingAsync(translationRoomId, Guid.NewGuid(), "start");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        _egressServiceMock.Verify(e => e.StartRoomCompositeEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetRecordingAsync_ReturnsForbidden_ForAParticipantWhoIsNotTheHost()
    {
        var translationRoomId = Guid.NewGuid();
        var meetingRoomId = Guid.NewGuid();
        var participantUserId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = meetingRoomId, TranslationRoomId = translationRoomId, ActiveHostId = Guid.NewGuid(), ProviderRoomName = "room-1" };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);
        SetupMeetingParticipant(_unitOfWorkMock, new RtcStreamParticipant
        {
            MeetingRoomId = meetingRoomId,
            UserId = participantUserId,
            IsActive = true,
            JoinedAt = DateTime.UtcNow,
        });

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));
        _egressServiceMock
            .Setup(e => e.StartRoomCompositeEgressAsync("room-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("egress-1"));

        var result = await _sut.SetRecordingAsync(translationRoomId, participantUserId, "start");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Null(meetingRoom.ActiveEgressId);
        _egressServiceMock.Verify(e => e.StartRoomCompositeEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        roomRepoMock.Verify(r => r.Update(meetingRoom), Times.Never);
    }

    // WT-234: a departing host used to hand the room to the earliest-joined participant.
    // ActiveHostId gates breakouts, polls, questions, recording and mute-all, so that quietly
    // handed real powers to someone who was still shown — and still stored — as a participant.

    [Fact]
    public async Task HandleHostOfflineAsync_ClearsHostWithoutPromotingAnyone_WhenDepartedUserWasHost()
    {
        var translationRoomId = Guid.NewGuid();
        var meetingRoomId = Guid.NewGuid();
        var departedHostId = Guid.NewGuid();
        var earlierUserId = Guid.NewGuid();
        var laterUserId = Guid.NewGuid();

        var meetingRoom = new MeetingRoom { Id = meetingRoomId, TranslationRoomId = translationRoomId, ActiveHostId = departedHostId, ProviderRoomName = "room-1" };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var participants = new List<RtcStreamParticipant>
        {
            new() { MeetingRoomId = meetingRoomId, UserId = laterUserId, IsActive = true, JoinedAt = DateTime.UtcNow },
            new() { MeetingRoomId = meetingRoomId, UserId = earlierUserId, IsActive = true, JoinedAt = DateTime.UtcNow.AddMinutes(-5) },
        };
        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants);
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        var result = await _sut.HandleHostOfflineAsync(translationRoomId, departedHostId);

        Assert.True(result.IsSuccess);
        // Host-less, even though two participants are still in the room and available.
        Assert.Null(meetingRoom.ActiveHostId);
        roomRepoMock.Verify(r => r.ClearActiveHostIfAsync(translationRoomId, departedHostId, It.IsAny<CancellationToken>()), Times.Once);
        roomRepoMock.Verify(r => r.Update(It.IsAny<MeetingRoom>()), Times.Never);
        _redisServiceMock.Verify(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
    }

    /// <summary>
    /// Multi-replica: every meeting-service replica receives the same participant-offline pub/sub
    /// message. Handling it twice must clear the host once and never touch a later claim.
    /// </summary>
    [Fact]
    public async Task HandleHostOfflineAsync_HandledByEveryReplica_ClearsOnce_AndKeepsAHostClaimedInBetween()
    {
        var translationRoomId = Guid.NewGuid();
        var departedHostId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = departedHostId, ProviderRoomName = "room-1" };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var first = await _sut.HandleHostOfflineAsync(translationRoomId, departedHostId);

        // Someone claims the host seat before the slower replica gets to the same message.
        var newHostId = Guid.NewGuid();
        meetingRoom.ActiveHostId = newHostId;
        var second = await _sut.HandleHostOfflineAsync(translationRoomId, departedHostId);

        Assert.True(first.Value);
        Assert.False(second.Value);
        Assert.Equal(newHostId, meetingRoom.ActiveHostId);
    }

    [Fact]
    public async Task HandleHostOfflineAsync_DoesNothing_WhenTheRoomIsAlreadyHostless()
    {
        var translationRoomId = Guid.NewGuid();
        var departedUserId = Guid.NewGuid();

        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = null, ProviderRoomName = "room-1" };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var result = await _sut.HandleHostOfflineAsync(translationRoomId, departedUserId);

        // A host-less room stays host-less: someone else leaving must not trigger an election.
        Assert.True(result.IsSuccess);
        Assert.Null(meetingRoom.ActiveHostId);
        roomRepoMock.Verify(r => r.Update(It.IsAny<MeetingRoom>()), Times.Never);
        _redisServiceMock.Verify(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task HandleHostOfflineAsync_DoesNothing_WhenDepartedUserWasNotHost_AndSomeoneElseIsHost()
    {
        var translationRoomId = Guid.NewGuid();
        var currentHostId = Guid.NewGuid();
        var departedUserId = Guid.NewGuid();

        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = currentHostId, ProviderRoomName = "room-1" };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var result = await _sut.HandleHostOfflineAsync(translationRoomId, departedUserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(currentHostId, meetingRoom.ActiveHostId);
        Assert.False(result.Value);
        roomRepoMock.Verify(r => r.Update(It.IsAny<MeetingRoom>()), Times.Never);
        _redisServiceMock.Verify(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task HandleHostOfflineAsync_ClearsHost_WhenNoActiveParticipantsRemain()
    {
        var translationRoomId = Guid.NewGuid();
        var departedHostId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = departedHostId, ProviderRoomName = "room-1" };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RtcStreamParticipant>());
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        var result = await _sut.HandleHostOfflineAsync(translationRoomId, departedHostId);

        Assert.True(result.IsSuccess);
        Assert.Null(meetingRoom.ActiveHostId);
        roomRepoMock.Verify(r => r.ClearActiveHostIfAsync(translationRoomId, departedHostId, It.IsAny<CancellationToken>()), Times.Once);
        roomRepoMock.Verify(r => r.Update(It.IsAny<MeetingRoom>()), Times.Never);
        _redisServiceMock.Verify(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task TransferHostAsync_AnnouncesTheNewHost()
    {
        var translationRoomId = Guid.NewGuid();
        var meetingRoomId = Guid.NewGuid();
        var currentHostId = Guid.NewGuid();
        var newHostId = Guid.NewGuid();

        var meetingRoom = new MeetingRoom { Id = meetingRoomId, TranslationRoomId = translationRoomId, ActiveHostId = currentHostId, ProviderRoomName = "room-1" };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcStreamParticipant { MeetingRoomId = meetingRoomId, UserId = newHostId, IsActive = true });
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        // WT-359: host authority lives in the translation-room service, so the transfer has to
        // reach it. It answers with the host it replaced.
        _grpcServiceMock
            .Setup(g => g.TransferRoomHostAsync(translationRoomId, currentHostId, newHostId))
            .ReturnsAsync(Result.Success(currentHostId));

        var result = await _sut.TransferHostAsync(translationRoomId, currentHostId, newHostId);

        Assert.True(result.IsSuccess);
        Assert.Equal(newHostId, meetingRoom.ActiveHostId);
        // The deliberate path is now the one that tells the room, so clients switch controls
        // immediately instead of waiting for a full room refetch (WT-234).
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<object>(payload => HasProperty(payload, "Command", "HostChanged") && HasProperty(payload, "NewHostUserId", newHostId.ToString()))),
            Times.Once);
    }

    /// <summary>
    /// WT-359. The transfer used to write only this service's active_host_id, which is the LIVE
    /// SESSION's host and not the one the translation-room service reads when it decides whether a
    /// joiner is the host. That is why the outgoing host was handed the room back on rejoin.
    /// </summary>
    [Fact]
    public async Task TransferHostAsync_MovesHostAuthorityInTheTranslationRoomService()
    {
        var translationRoomId = Guid.NewGuid();
        var meetingRoomId = Guid.NewGuid();
        var currentHostId = Guid.NewGuid();
        var newHostId = Guid.NewGuid();

        var meetingRoom = new MeetingRoom { Id = meetingRoomId, TranslationRoomId = translationRoomId, ActiveHostId = currentHostId, ProviderRoomName = "room-1" };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcStreamParticipant { MeetingRoomId = meetingRoomId, UserId = newHostId, IsActive = true });
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        _grpcServiceMock
            .Setup(g => g.TransferRoomHostAsync(translationRoomId, currentHostId, newHostId))
            .ReturnsAsync(Result.Success(currentHostId));

        var result = await _sut.TransferHostAsync(translationRoomId, currentHostId, newHostId);

        Assert.True(result.IsSuccess);
        _grpcServiceMock.Verify(
            g => g.TransferRoomHostAsync(translationRoomId, currentHostId, newHostId), Times.Once);

        // WT-358: both sides named, so a client can demote the outgoing host as well as promote the
        // incoming one. The presence payload carries no role, so one id was never enough.
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<object>(payload => HasProperty(payload, "PreviousHostUserId", currentHostId.ToString()))),
            Times.Once);
    }

    /// <summary>
    /// The remote write is ordered FIRST precisely so this case leaves nothing changed anywhere.
    /// A local commit followed by a remote failure would reproduce the very split WT-359 is about,
    /// and would do it silently.
    /// </summary>
    [Fact]
    public async Task TransferHostAsync_LeavesLocalStateUntouched_WhenTheRoomServiceRefuses()
    {
        var translationRoomId = Guid.NewGuid();
        var meetingRoomId = Guid.NewGuid();
        var currentHostId = Guid.NewGuid();
        var newHostId = Guid.NewGuid();

        var meetingRoom = new MeetingRoom { Id = meetingRoomId, TranslationRoomId = translationRoomId, ActiveHostId = currentHostId, ProviderRoomName = "room-1" };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = Guid.NewGuid().ToString() }));

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcStreamParticipant { MeetingRoomId = meetingRoomId, UserId = newHostId, IsActive = true });
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        _grpcServiceMock
            .Setup(g => g.TransferRoomHostAsync(translationRoomId, currentHostId, newHostId))
            .ReturnsAsync(Result.Failure<Guid>("Only the current host can transfer this room.", "TRANSFER_FORBIDDEN"));

        var result = await _sut.TransferHostAsync(translationRoomId, currentHostId, newHostId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(currentHostId, meetingRoom.ActiveHostId);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task TriggerAiAsync_PublishesTrackPublishedEvent()
    {
        var translationRoomId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var request = new TriggerAiRequest
        {
            ParticipantIdentity = Guid.NewGuid().ToString()
        };
        _grpcServiceMock
            .Setup(service => service.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse
            {
                WorkspaceId = workspaceId.ToString(),
                Title = "WarpTalk engineering review",
                Description = "Discuss Docker, Kubernetes, Redis, and LiveKit."
            }));

        var result = await _sut.TriggerAiAsync(translationRoomId, request);

        Assert.True(result.IsSuccess);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                MeetingEventTypes.Started,
                It.Is<EventEnvelope<MeetingStartedEventPayload>>(envelope =>
                    envelope.Payload.TranslationRoomId == translationRoomId &&
                    envelope.Payload.WorkspaceId == workspaceId &&
                    envelope.Payload.Title == "WarpTalk engineering review" &&
                    envelope.Payload.Description == "Discuss Docker, Kubernetes, Redis, and LiveKit.")),
            Times.Once);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                MeetingEventTypes.TrackPublished,
                It.Is<EventEnvelope<MeetingTrackPublishedEventPayload>>(envelope =>
                    envelope.EventType == MeetingEventTypes.TrackPublished &&
                    envelope.SchemaVersion == 1 &&
                    envelope.Payload.RoomName == translationRoomId.ToString() &&
                    envelope.Payload.ParticipantIdentity == request.ParticipantIdentity &&
                    envelope.Payload.TrackId == "audio_track_1")),
            Times.Once);
    }

    [Fact]
    public async Task TriggerAiAsync_ReturnsFailure_WhenPublishFails()
    {
        var translationRoomId = Guid.NewGuid();
        _grpcServiceMock
            .Setup(service => service.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse
            {
                WorkspaceId = Guid.NewGuid().ToString()
            }));
        _redisServiceMock
            .Setup(r => r.PublishEventAsync(
                MeetingEventTypes.TrackPublished,
                It.IsAny<EventEnvelope<MeetingTrackPublishedEventPayload>>()))
            .ReturnsAsync(Result.Failure("Redis unavailable", "REDIS_ERROR"));

        var result = await _sut.TriggerAiAsync(translationRoomId, new TriggerAiRequest
        {
            ParticipantIdentity = Guid.NewGuid().ToString()
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("Redis unavailable", result.Error);
        Assert.Equal("REDIS_ERROR", result.ErrorCode);
    }

    [Fact]
    public async Task JoinMeetingAsync_RejectsDeclinedInvitation()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var meetingRoomId = Guid.NewGuid();

        var roomDetails = new WarpTalk.Shared.Protos.GetTranslationRoomResponse
        {
            HostId = hostId.ToString(),
            Status = "IN_PROGRESS",
            WorkspaceId = Guid.NewGuid().ToString()
        };
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(roomDetails));

        var meetingRoom = new MeetingRoom
        {
            Id = meetingRoomId,
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "IN_PROGRESS"
        };
        var roomRepoMock = new Mock<IMeetingRoomRepository>();
        roomRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(meetingRoom);
        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(roomRepoMock.Object);

        var invitation = new RtcSessionRevocation
        {
            Id = Guid.NewGuid(),
            MeetingRoomId = meetingRoomId,
            InviteeUserId = userId,
            Status = "DECLINED"
        };
        var invitationRepoMock = new Mock<IRtcSessionRevocationRepository>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invitation);
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository).Returns(invitationRepoMock.Object);

        var result = await _sut.JoinMeetingAsync(translationRoomId, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        _unitOfWorkMock.Verify(u => u.RollbackTransactionAsync(), Times.Once);
    }

    /// <summary>A room that is live, hosted by <paramref name="hostId"/>, as EndMeetingAsync reads it.</summary>
    private Mock<IMeetingRoomRepository> ArrangeLiveMeeting(Guid translationRoomId, Guid hostId, Func<bool> tryMarkFinished)
    {
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse
                {
                    HostId = hostId.ToString(),
                    Status = "IN_PROGRESS",
                    WorkspaceId = Guid.NewGuid().ToString()
                }));

        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "IN_PROGRESS"
        };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);
        roomRepoMock
            .Setup(r => r.TryMarkFinishedAsync(meetingRoom.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tryMarkFinished);
        return roomRepoMock;
    }

    private void VerifySentinelPublished(Guid translationRoomId, Times times) =>
        _redisServiceMock.Verify(
            r => r.PublishStreamMessageAsync(
                "stt:results",
                It.Is<Dictionary<string, string>>(fields =>
                    fields["meeting_id"] == translationRoomId.ToString() &&
                    fields["text"] == "__MEETING_END__")),
            times);

    [Fact]
    public async Task EndMeetingAsync_TriggersAiSummary_WithoutUnusedMeetingEndedPubSub()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        ArrangeLiveMeeting(translationRoomId, hostId, () => true);

        var result = await _sut.EndMeetingAsync(translationRoomId, hostId);

        Assert.True(result.IsSuccess);

        _redisServiceMock.Verify(
            r => r.PublishEventAsync("meeting.ended", It.IsAny<object>()),
            Times.Never);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync("meeting.end_room", It.IsAny<object>()),
            Times.Never);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync("meeting.billing.stop", It.IsAny<object>()),
            Times.Never);
        _roomAdminServiceMock.Verify(
            r => r.DeleteRoomAsync(translationRoomId.ToString(), It.IsAny<CancellationToken>()),
            Times.Once);

        VerifySentinelPublished(translationRoomId, Times.Once());
    }

    /// <summary>
    /// k8s multi-replica dedupe. Production: 12 of the 42 meetings still in stt:results carried two
    /// to four __MEETING_END__ sentinels 0–1.8 s apart, because every "End for everyone" call —
    /// on either replica — published its own. The meeting ends once, so it is summarised once.
    /// </summary>
    [Fact]
    public async Task EndMeetingAsync_ConcurrentCalls_PublishTheSummaryTriggerOnce()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        // Stands in for the conditional UPDATE: only the first caller finds ended_at still null.
        var ended = 0;
        ArrangeLiveMeeting(translationRoomId, hostId, () => Interlocked.Exchange(ref ended, 1) == 0);

        var results = await Task.WhenAll(
            _sut.EndMeetingAsync(translationRoomId, hostId),
            _sut.EndMeetingAsync(translationRoomId, hostId),
            _sut.EndMeetingAsync(translationRoomId, hostId));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        VerifySentinelPublished(translationRoomId, Times.Once());
    }

    [Fact]
    public async Task EndMeetingAsync_WhenAlreadyEnded_SucceedsWithoutTriggeringTheSummaryAgain()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        ArrangeLiveMeeting(translationRoomId, hostId, () => false);

        var result = await _sut.EndMeetingAsync(translationRoomId, hostId);

        Assert.True(result.IsSuccess);
        VerifySentinelPublished(translationRoomId, Times.Never());
    }

    [Fact]
    public async Task EndMeetingAsync_WithNoMeetingRoomRow_DoesNotTriggerTheSummary()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = hostId.ToString(), Status = "IN_PROGRESS" }));
        SetupMeetingRoomRepository(_unitOfWorkMock, null);

        var result = await _sut.EndMeetingAsync(translationRoomId, hostId);

        Assert.True(result.IsSuccess);
        VerifySentinelPublished(translationRoomId, Times.Never());
    }

    /// <summary>
    /// WT-870: "Save the meeting transcript" off means no summary. The marker that makes
    /// ai_assistant_worker summarise the meeting is not sent; the meeting still ends.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task EndMeetingAsync_RequestsAiSummaryOnlyWhenTheRoomSavesItsTranscript(bool saveTranscript, int expectedTriggers)
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();

        var roomDetails = new WarpTalk.Shared.Protos.GetTranslationRoomResponse
        {
            HostId = hostId.ToString(),
            Status = "IN_PROGRESS",
            WorkspaceId = Guid.NewGuid().ToString(),
            SaveTranscript = saveTranscript
        };
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(roomDetails));

        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "IN_PROGRESS"
        };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom)
            .Setup(r => r.TryMarkFinishedAsync(meetingRoom.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.EndMeetingAsync(translationRoomId, hostId);

        Assert.True(result.IsSuccess);
        _roomAdminServiceMock.Verify(
            r => r.DeleteRoomAsync(translationRoomId.ToString(), It.IsAny<CancellationToken>()),
            Times.Once);
        _redisServiceMock.Verify(
            r => r.PublishStreamMessageAsync(
                "stt:results",
                It.Is<Dictionary<string, string>>(fields => fields["text"] == "__MEETING_END__")),
            Times.Exactly(expectedTriggers));
    }

    [Fact]
    public async Task EndMeetingAsync_StillSucceeds_WhenAiSummaryTriggerFails()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        ArrangeLiveMeeting(translationRoomId, hostId, () => true);

        _redisServiceMock
            .Setup(r => r.PublishStreamMessageAsync("stt:results", It.IsAny<Dictionary<string, string>>()))
            .ThrowsAsync(new InvalidOperationException("Redis unavailable"));

        var result = await _sut.EndMeetingAsync(translationRoomId, hostId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task EndMeetingAsync_DoesNotPersistFinishedState_WhenLiveKitDeleteFails()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = "provider-room",
            ActiveHostId = hostId,
            Status = "IN_PROGRESS"
        };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse
                {
                    HostId = hostId.ToString(),
                    WorkspaceId = Guid.NewGuid().ToString()
                }));
        _roomAdminServiceMock
            .Setup(service => service.DeleteRoomAsync("provider-room", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<bool>("LiveKit Unauthorized", "LIVEKIT_ROOM_COMMAND_FAILED"));

        var result = await _sut.EndMeetingAsync(translationRoomId, hostId);

        Assert.False(result.IsSuccess);
        Assert.Equal("IN_PROGRESS", meetingRoom.Status);
        Assert.Null(meetingRoom.EndedAt);
        _unitOfWorkMock.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task KickParticipantAsync_RemovesParticipantFromLiveKit_WithoutDeadPubSub()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var participantUserId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ActiveHostId = hostId,
            ProviderRoomName = "provider-room"
        };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse
                {
                    HostId = hostId.ToString(),
                    WorkspaceId = Guid.NewGuid().ToString()
                }));

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcStreamParticipant
            {
                MeetingRoomId = meetingRoom.Id,
                UserId = participantUserId,
                IsActive = true
            });
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        var invitationRepoMock = new Mock<IRtcSessionRevocationRepository>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcSessionRevocation?)null);
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository).Returns(invitationRepoMock.Object);

        // WT-564: the kick now carries the TERMINAL status to the room service before it touches
        // anything local, because that is the only write that stops a rejoin. Unstubbed, this is
        // where the kick stops.
        _grpcServiceMock
            .Setup(g => g.KickRoomParticipantAsync(translationRoomId, hostId, participantUserId))
            .ReturnsAsync(Result.Success(RoomRosterRemoval.Removed));

        var result = await _sut.KickParticipantAsync(translationRoomId, hostId, participantUserId);

        Assert.True(result.IsSuccess);
        _roomAdminServiceMock.Verify(
            r => r.RemoveParticipantAsync(
                "provider-room",
                participantUserId.ToString(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync("meeting.kick_participant", It.IsAny<object>()),
            Times.Never);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync("meeting.chat.participant_kicked", It.IsAny<object>()),
            Times.Never);
    }

    // WT-282: the join response must report the room's lock state so the in-room host-controls
    // menu can render the true state on first open instead of assuming a default. The server
    // already reads MeetingRoom.IsLocked to gate the join; these pin that it also reports it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JoinMeetingAsync_ReportsRoomLockState_ToHost(bool isLocked)
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse
                {
                    HostId = hostId.ToString(),
                    Status = "IN_PROGRESS",
                    WorkspaceId = Guid.NewGuid().ToString(),
                    // WT-428: the lobby now keys on this setting, not on room status.
                    RequiresApproval = true
                }));

        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "IN_PROGRESS",
            ActiveHostId = hostId,
            IsLocked = isLocked
        };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcStreamParticipant
            {
                Id = Guid.NewGuid(),
                MeetingRoomId = meetingRoom.Id,
                UserId = hostId,
                ProviderIdentity = hostId.ToString(),
                IsActive = true,
                JoinedAt = DateTime.UtcNow
            });
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        var invitationRepoMock = new Mock<IRtcSessionRevocationRepository>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcSessionRevocation?)null);
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository).Returns(invitationRepoMock.Object);

        _tokenServiceMock
            .Setup(service => service.GenerateToken(
                translationRoomId.ToString(),
                hostId.ToString(),
                "Host",
                true,
                true))
            .Returns(Result.Success("livekit-token"));

        var result = await _sut.JoinMeetingAsync(translationRoomId, hostId, "Host");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsWaitingRoom);
        Assert.Equal(isLocked, result.Value.Locked);
    }

    // WT-283: a joiner must be told the room is being recorded. Unlike lock and mute-on-entry,
    // recording is not a bool column — it is derived from MeetingRoom.ActiveEgressId being
    // non-empty, the same derivation SetRecordingAsync uses. See JoinMeetingResponse.Recording
    // for why the egress id itself is deliberately NOT carried in the join response.
    //
    // The load-bearing row is the FIRST one: a room WITH an active egress must report
    // Recording == true. An unpopulated bool defaults to false, so only the positive row can
    // fail against code that does not populate the field — the null row would pass either way
    // and proves nothing on its own.
    [Theory]
    [InlineData("EG_wt283_active_egress", true)]
    [InlineData(null, false)]
    public async Task JoinMeetingAsync_ReportsRecordingState_ToAdmittedParticipant(
        string? activeEgressId,
        bool expectedRecording)
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var participantUserId = Guid.NewGuid();

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse
                {
                    HostId = hostId.ToString(),
                    Status = "IN_PROGRESS",
                    WorkspaceId = Guid.NewGuid().ToString(),
                    // WT-428: the lobby now keys on this setting, not on room status.
                    RequiresApproval = true
                }));

        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "IN_PROGRESS",
            ActiveHostId = hostId,
            ActiveEgressId = activeEgressId
        };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcStreamParticipant
            {
                Id = Guid.NewGuid(),
                MeetingRoomId = meetingRoom.Id,
                UserId = participantUserId,
                ProviderIdentity = participantUserId.ToString(),
                IsActive = true,
                JoinedAt = DateTime.UtcNow
            });
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        var invitationRepoMock = new Mock<IRtcSessionRevocationRepository>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcSessionRevocation?)null);
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository).Returns(invitationRepoMock.Object);

        // WT-428: the lobby gate is now unconditional for a requires-approval room, so being
        // "admitted" is no longer implied by the room being IN_PROGRESS — the translation-room
        // roster has to actually say so, exactly as it does in production once the host approves.
        var admittedRoster = new WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse();
        admittedRoster.Participants.Add(new WarpTalk.Shared.Protos.Participant
        {
            Id = participantUserId.ToString(),
            IsActive = true,
        });
        _grpcServiceMock
            .Setup(g => g.GetParticipantsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(admittedRoster));

        _tokenServiceMock
            .Setup(service => service.GenerateToken(
                translationRoomId.ToString(),
                participantUserId.ToString(),
                "Participant name",
                true,
                true))
            .Returns(Result.Success("livekit-token"));

        var result = await _sut.JoinMeetingAsync(translationRoomId, participantUserId, "Participant name");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsWaitingRoom);
        Assert.Equal(expectedRecording, result.Value.Recording);
    }

    /// <summary>
    /// WT-428 (Linear) — the production bypass, verbatim. The lobby used to be keyed on room
    /// status (SCHEDULED/WAITING only), so the moment a requires-approval room went IN_PROGRESS,
    /// an invitee who pressed Join was handed a LiveKit token the host never approved. The
    /// display name is deliberately PROVIDED here: the only other admission check lived inside
    /// the empty-display-name branch, which the web client never enters.
    /// </summary>
    [Fact]
    public async Task JoinMeetingAsync_HoldsUnapprovedInviteeInLobby_EvenWhenRoomIsInProgress()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var participantUserId = Guid.NewGuid();

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse
                {
                    HostId = hostId.ToString(),
                    Status = "IN_PROGRESS",
                    WorkspaceId = Guid.NewGuid().ToString(),
                    RequiresApproval = true
                }));

        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "IN_PROGRESS",
            ActiveHostId = hostId,
        };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcStreamParticipant?)null);
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        var invitationRepoMock = new Mock<IRtcSessionRevocationRepository>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcSessionRevocation
            {
                Id = Guid.NewGuid(),
                MeetingRoomId = meetingRoom.Id,
                InviteeUserId = participantUserId,
                Status = "PENDING",
            });
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository).Returns(invitationRepoMock.Object);

        // The translation-room roster does NOT show them admitted — the host never approved.
        _grpcServiceMock
            .Setup(g => g.GetParticipantsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse()));

        var result = await _sut.JoinMeetingAsync(translationRoomId, participantUserId, "Invitee Name");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsWaitingRoom, "an unapproved invitee must be held in the lobby, not handed a token");
        Assert.Equal(string.Empty, result.Value.Token);
    }

    // WT-283: the lobby response is built at its own construction site, so a participant held in
    // the waiting room of a room that is already recording must be told too — that is exactly the
    // moment they decide whether to be admitted.
    [Fact]
    public async Task JoinMeetingAsync_ReportsRecordingState_ToWaitingRoomJoiner_WhenRoomIsWaiting()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var participantUserId = Guid.NewGuid();

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse
                {
                    HostId = hostId.ToString(),
                    Status = "WAITING",
                    WorkspaceId = Guid.NewGuid().ToString(),
                    // WT-428: the lobby now keys on this setting, not on room status.
                    RequiresApproval = true
                }));

        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "WAITING",
            ActiveHostId = hostId,
            ActiveEgressId = "EG_wt283_active_egress"
        };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcStreamParticipant
            {
                Id = Guid.NewGuid(),
                MeetingRoomId = meetingRoom.Id,
                UserId = participantUserId,
                ProviderIdentity = participantUserId.ToString(),
                IsActive = true,
                JoinedAt = DateTime.UtcNow
            });
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        var invitationRepoMock = new Mock<IRtcSessionRevocationRepository>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcSessionRevocation?)null);
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository).Returns(invitationRepoMock.Object);

        // Not yet admitted by the host: Translation Room reports no active participant row.
        _grpcServiceMock
            .Setup(g => g.GetParticipantsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse()));

        var result = await _sut.JoinMeetingAsync(translationRoomId, participantUserId, "Participant name");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsWaitingRoom);
        Assert.True(result.Value.Recording);
    }

    // WT-283: the third construction site — an in-progress room where the caller supplied no
    // display name and Translation Room reports them as not-yet-active, so the join falls back to
    // the lobby response built inside the display-name resolution block.
    [Fact]
    public async Task JoinMeetingAsync_ReportsRecordingState_ToWaitingRoomJoiner_WhenNotYetActiveInTranslationRoom()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var participantUserId = Guid.NewGuid();

        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(
                new WarpTalk.Shared.Protos.GetTranslationRoomResponse
                {
                    HostId = hostId.ToString(),
                    Status = "IN_PROGRESS",
                    WorkspaceId = Guid.NewGuid().ToString(),
                    // WT-428: the lobby now keys on this setting, not on room status.
                    RequiresApproval = true
                }));

        var meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "IN_PROGRESS",
            ActiveHostId = hostId,
            ActiveEgressId = "EG_wt283_active_egress"
        };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcStreamParticipant
            {
                Id = Guid.NewGuid(),
                MeetingRoomId = meetingRoom.Id,
                UserId = participantUserId,
                ProviderIdentity = participantUserId.ToString(),
                IsActive = true,
                JoinedAt = DateTime.UtcNow
            });
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        var invitationRepoMock = new Mock<IRtcSessionRevocationRepository>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcSessionRevocation?)null);
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository).Returns(invitationRepoMock.Object);

        var translationParticipants = new WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse();
        translationParticipants.Participants.Add(new WarpTalk.Shared.Protos.Participant
        {
            Id = participantUserId.ToString(),
            DisplayName = "Pending participant",
            IsActive = false
        });
        _grpcServiceMock
            .Setup(g => g.GetParticipantsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(translationParticipants));

        var result = await _sut.JoinMeetingAsync(translationRoomId, participantUserId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsWaitingRoom);
        Assert.True(result.Value.Recording);
    }

    private static bool HasProperty(object payload, string propertyName, string expectedValue)
    {
        // PublishGatewayCommandAsync builds a Dictionary<string, object?> (see
        // MeetingRoomService) rather than an anonymous type for the Gateway commands
        // channel, unlike the plain anonymous-object payloads used elsewhere (e.g.
        // versioned meeting events) — so this helper needs to check both shapes.
        if (payload is System.Collections.IDictionary dictionary)
        {
            return dictionary.Contains(propertyName) &&
                   string.Equals(dictionary[propertyName]?.ToString(), expectedValue, StringComparison.Ordinal);
        }

        var property = payload.GetType().GetProperty(propertyName);
        return string.Equals(property?.GetValue(payload)?.ToString(), expectedValue, StringComparison.Ordinal);
    }

    // ---- WT-525: bridge token ----------------------------------------------------------------
    //
    // This is the one method that mints a LiveKit token for an identity other than the caller's,
    // so the tests that matter most are the two REFUSALS. A regression that widens either gate
    // would not break any of the happy-path assertions.

    private static readonly Guid BridgeWorkspaceId = Guid.NewGuid();

    private void SetupBridgeRoom(Guid translationRoomId, string hostId, string roomType, string status = "IN_PROGRESS", string capturerUserId = "")
    {
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse
            {
                HostId = hostId,
                Status = status,
                TranslationRoomType = roomType,
                BridgeCapturerUserId = capturerUserId,
                // WT-916: the bridge token now provisions the row and publishes MeetingStarted,
                // which needs a real workspace id exactly as the join path always has.
                WorkspaceId = BridgeWorkspaceId.ToString(),
            }));
        _tokenServiceMock
            .Setup(t => t.GenerateToken(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(Result.Success("a-token"));
    }

    [Fact]
    public async Task GenerateBridgeTokenAsync_MintsTheStandInIdentity_ForTheHostOfABridgeRoom()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(translationRoomId, hostId.ToString(), ExternalBridgeConstants.RoomType);

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, hostId);

        Assert.True(result.IsSuccess);
        // The identity is the contract the AI pipeline routes on — assert the value, not just success.
        Assert.Equal(ExternalBridgeConstants.ParticipantUserId.ToString(), result.Value!.ParticipantIdentity);
        // Publish-only: subscribing the stand-in would send the room's dubs back into the device
        // Meet is playing into, which is a feedback loop rather than a wasted subscription.
        _tokenServiceMock.Verify(t => t.GenerateToken(
            It.IsAny<string>(),
            ExternalBridgeConstants.ParticipantUserId.ToString(),
            ExternalBridgeConstants.DisplayName,
            true,
            false), Times.Once);
    }

    [Fact]
    public async Task GenerateBridgeTokenAsync_RefusesANonHost_EvenInABridgeRoom()
    {
        var translationRoomId = Guid.NewGuid();
        SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(translationRoomId, Guid.NewGuid().ToString(), ExternalBridgeConstants.RoomType);

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        _tokenServiceMock.Verify(t => t.GenerateToken(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
    }

    // ---- One shared bridge room per Meet code: the CAPTURER mints the stand-in ------------------

    [Fact]
    public async Task GenerateBridgeTokenAsync_MintsForTheCapturer_WhoIsNotTheHost()
    {
        var translationRoomId = Guid.NewGuid();
        var capturer = Guid.NewGuid();
        SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(translationRoomId, Guid.NewGuid().ToString(), ExternalBridgeConstants.RoomType, capturerUserId: capturer.ToString());

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, capturer);

        Assert.True(result.IsSuccess);
        Assert.Equal(ExternalBridgeConstants.ParticipantUserId.ToString(), result.Value!.ParticipantIdentity);
    }

    [Fact]
    public async Task GenerateBridgeTokenAsync_RefusesAMember_AndTheHost_WhenSomeoneElseCaptures()
    {
        // Two stand-in publishers would double the far side: only the capturer may mint.
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(translationRoomId, hostId.ToString(), ExternalBridgeConstants.RoomType, capturerUserId: Guid.NewGuid().ToString());

        var member = await _sut.GenerateBridgeTokenAsync(translationRoomId, Guid.NewGuid());
        var host = await _sut.GenerateBridgeTokenAsync(translationRoomId, hostId);

        Assert.Equal(ErrorCodes.Forbidden, member.ErrorCode);
        Assert.Equal(ErrorCodes.Forbidden, host.ErrorCode);
        _tokenServiceMock.Verify(t => t.GenerateToken(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateBridgeTokenAsync_LegacyRoomWithoutACapturer_StillMintsForTheHost()
    {
        // capturerUserId "" = a room from before bridge claim (or an older server): the host rule.
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(translationRoomId, hostId.ToString(), ExternalBridgeConstants.RoomType, capturerUserId: "");

        Assert.True((await _sut.GenerateBridgeTokenAsync(translationRoomId, hostId)).IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, (await _sut.GenerateBridgeTokenAsync(translationRoomId, Guid.NewGuid())).ErrorCode);
    }

    [Fact]
    public async Task GenerateBridgeTokenAsync_RefusesTheHost_WhenTheRoomIsNotABridge()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(translationRoomId, hostId.ToString(), "EVENT");

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, hostId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        _tokenServiceMock.Verify(t => t.GenerateToken(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateBridgeTokenAsync_TreatsAnEmptyRoomTypeAsNotABridge()
    {
        // An older translation-room server does not send field 12, and proto3 delivers "". The
        // failure being guarded is minting a second identity into someone else's meeting, so the
        // absence of the field must read as "no" rather than as "unknown, allow".
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(translationRoomId, hostId.ToString(), string.Empty);

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, hostId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    // ---- WT-916 (B20): the bridge token provisions the meeting_rooms row ----------------------
    //
    // The far side enters LiveKit through the bridge token, so a room whose capturer's web window
    // had not joined yet had audio in LiveKit and no row: recording 404'd, webhooks and egress
    // completion found nothing, and no MeetingStarted snapshot went out. These tests run against a
    // stateful repository so "exactly one row" is a count, not an assumption about call order.

    /// <summary>A meeting_rooms table in a list: FirstOrDefault/Find/Add behave like the real thing.</summary>
    private static (Mock<IMeetingRoomRepository> Repo, List<MeetingRoom> Rows) SetupStatefulMeetingRoomRepository(
        Mock<IUnitOfWork> unitOfWorkMock,
        params MeetingRoom[] existing)
    {
        var rows = new List<MeetingRoom>(existing);
        var repo = new Mock<IMeetingRoomRepository>();
        repo.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                rows.FirstOrDefault(predicate.Compile()));
        repo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                (IReadOnlyList<MeetingRoom>)rows.Where(predicate.Compile()).ToList());
        repo.Setup(r => r.AddAsync(It.IsAny<MeetingRoom>(), It.IsAny<CancellationToken>()))
            .Callback((MeetingRoom room, CancellationToken _) =>
            {
                if (room.Id == Guid.Empty)
                    room.Id = Guid.NewGuid();
                rows.Add(room);
            })
            .Returns(Task.CompletedTask);
        unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(repo.Object);
        return (repo, rows);
    }

    private void VerifyMeetingStartedPublished(Times times)
        => _redisServiceMock.Verify(
            r => r.PublishEventAsync(MeetingEventTypes.Started, It.IsAny<object>()),
            times);

    [Fact]
    public async Task GenerateBridgeTokenAsync_WithNoMeetingRoomRow_CreatesExactlyOneRow_AndPublishesMeetingStartedOnce()
    {
        var translationRoomId = Guid.NewGuid();
        var capturer = Guid.NewGuid();
        var (repo, rows) = SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(translationRoomId, Guid.NewGuid().ToString(), ExternalBridgeConstants.RoomType, capturerUserId: capturer.ToString());

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, capturer);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(rows);
        Assert.Equal(translationRoomId, row.TranslationRoomId);
        Assert.Equal(translationRoomId.ToString(), row.ProviderRoomName);
        Assert.Equal("IN_PROGRESS", row.Status);
        Assert.Equal(row.ProviderRoomName, result.Value!.ProviderRoomName);
        VerifyMeetingStartedPublished(Times.Once());
        // Created under the provisioning lock, inside a transaction that was committed.
        repo.Verify(r => r.AcquireProvisioningLockAsync(translationRoomId, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateBridgeTokenAsync_ReusesAnExistingRow_WithoutTheLockOrASecondMeetingStarted()
    {
        var translationRoomId = Guid.NewGuid();
        var capturer = Guid.NewGuid();
        var existing = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = "provisioned-under-another-name",
            Status = "IN_PROGRESS"
        };
        var (repo, rows) = SetupStatefulMeetingRoomRepository(_unitOfWorkMock, existing);
        SetupBridgeRoom(translationRoomId, Guid.NewGuid().ToString(), ExternalBridgeConstants.RoomType, capturerUserId: capturer.ToString());

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, capturer);

        Assert.True(result.IsSuccess);
        Assert.Same(existing, Assert.Single(rows));
        Assert.Equal("provisioned-under-another-name", result.Value!.ProviderRoomName);
        VerifyMeetingStartedPublished(Times.Never());
        repo.Verify(r => r.AddAsync(It.IsAny<MeetingRoom>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.AcquireProvisioningLockAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateBridgeTokenAsync_FindsTheRowARacingJoinCommitted_WhileItWaitedForTheLock()
    {
        // The race this change has to survive: no row on the first read, but by the time the lock
        // is granted the join that held it has committed one. The re-read must find it.
        var translationRoomId = Guid.NewGuid();
        var capturer = Guid.NewGuid();
        var (repo, rows) = SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        var committedByJoin = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = translationRoomId,
            ProviderRoomName = translationRoomId.ToString(),
            Status = "IN_PROGRESS"
        };
        repo.Setup(r => r.AcquireProvisioningLockAsync(translationRoomId, It.IsAny<CancellationToken>()))
            .Callback(() => rows.Add(committedByJoin))
            .Returns(Task.CompletedTask);
        SetupBridgeRoom(translationRoomId, Guid.NewGuid().ToString(), ExternalBridgeConstants.RoomType, capturerUserId: capturer.ToString());

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, capturer);

        Assert.True(result.IsSuccess);
        Assert.Same(committedByJoin, Assert.Single(rows));
        repo.Verify(r => r.AddAsync(It.IsAny<MeetingRoom>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyMeetingStartedPublished(Times.Never());
    }

    [Theory]
    [InlineData("not-capturer")]
    [InlineData("ended")]
    [InlineData("not-bridge")]
    public async Task GenerateBridgeTokenAsync_RefusedCallers_CreateNoRow_AndPublishNothing(string refusal)
    {
        var translationRoomId = Guid.NewGuid();
        var capturer = Guid.NewGuid();
        var (repo, rows) = SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(
            translationRoomId,
            Guid.NewGuid().ToString(),
            refusal == "not-bridge" ? "EVENT" : ExternalBridgeConstants.RoomType,
            status: refusal == "ended" ? "ENDED" : "IN_PROGRESS",
            capturerUserId: capturer.ToString());
        var caller = refusal == "not-capturer" ? Guid.NewGuid() : capturer;

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, caller);

        Assert.False(result.IsSuccess);
        Assert.Equal(refusal == "ended" ? ErrorCodes.InvalidState : ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Empty(rows);
        repo.Verify(r => r.AddAsync(It.IsAny<MeetingRoom>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.AcquireProvisioningLockAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWorkMock.Verify(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        VerifyMeetingStartedPublished(Times.Never());
    }

    [Fact]
    public async Task GenerateBridgeTokenAsync_FailsAndRollsBack_WhenMeetingStartedCannotBePublished()
    {
        // Same contract as join: a row without its snapshot would make the next caller find the
        // row and never publish one, so the provisioning transaction is rolled back and the token
        // is not issued.
        var translationRoomId = Guid.NewGuid();
        var capturer = Guid.NewGuid();
        SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        SetupBridgeRoom(translationRoomId, Guid.NewGuid().ToString(), ExternalBridgeConstants.RoomType, capturerUserId: capturer.ToString());
        _redisServiceMock
            .Setup(r => r.PublishEventAsync(MeetingEventTypes.Started, It.IsAny<object>()))
            .ReturnsAsync(Result.Failure("redis down"));

        var result = await _sut.GenerateBridgeTokenAsync(translationRoomId, capturer);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
        _unitOfWorkMock.Verify(u => u.RollbackTransactionAsync(It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.CommitTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        _tokenServiceMock.Verify(t => t.GenerateToken(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task JoinMeetingAsync_AfterTheBridgeToken_ReusesTheRowItProvisioned()
    {
        var translationRoomId = Guid.NewGuid();
        var capturer = Guid.NewGuid();
        var (repo, rows) = SetupStatefulMeetingRoomRepository(_unitOfWorkMock);
        // The capturer is the host here, so the join takes the host path (no lobby, no invite).
        SetupBridgeRoom(translationRoomId, capturer.ToString(), ExternalBridgeConstants.RoomType, capturerUserId: capturer.ToString());
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));
        SetupMeetingParticipant(_unitOfWorkMock, null);

        var bridge = await _sut.GenerateBridgeTokenAsync(translationRoomId, capturer);
        var join = await _sut.JoinMeetingAsync(translationRoomId, capturer, "Capturer");

        Assert.True(bridge.IsSuccess);
        Assert.True(join.IsSuccess, join.Error);
        var row = Assert.Single(rows);
        Assert.Equal(row.ProviderRoomName, join.Value!.ProviderRoomName);
        Assert.Equal(capturer, row.ActiveHostId);
        repo.Verify(r => r.AddAsync(It.IsAny<MeetingRoom>(), It.IsAny<CancellationToken>()), Times.Once);
        VerifyMeetingStartedPublished(Times.Once());
    }
}
