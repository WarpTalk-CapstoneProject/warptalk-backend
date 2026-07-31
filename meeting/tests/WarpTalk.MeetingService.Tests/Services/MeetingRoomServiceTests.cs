using System;
using System.Collections.Generic;
using System.Linq.Expressions;
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
        unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(roomRepoMock.Object);
        return roomRepoMock;
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

        var participantRepoMock = new Mock<IMeetingParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingParticipant?)null);
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository).Returns(participantRepoMock.Object);

        var participantsCacheKey = $"meeting:participants:{translationRoomId}";
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse?>(null));
        _grpcServiceMock
            .Setup(g => g.GetParticipantsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetParticipantsByRoomIdResponse()));

        var invitationRepoMock = new Mock<IGenericRepository<MeetingInvitation>>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingInvitation, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingInvitation?)null);
        _unitOfWorkMock.Setup(u => u.Repository<MeetingInvitation>()).Returns(invitationRepoMock.Object);

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
                    WorkspaceId = Guid.NewGuid().ToString()
                }));
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var meetingParticipant = new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            MeetingRoomId = meetingRoom.Id,
            UserId = userId,
            ProviderIdentity = userId.ToString(),
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        };
        var participantRepoMock = new Mock<IMeetingParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(meetingParticipant);
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository)
            .Returns(participantRepoMock.Object);

        var invitationRepoMock = new Mock<IGenericRepository<MeetingInvitation>>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingInvitation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingInvitation?)null);
        _unitOfWorkMock.Setup(u => u.Repository<MeetingInvitation>())
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

    [Fact]
    public async Task SetRecordingAsync_StopsEgress_AndClearsEgressId()
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
        Assert.False(result.Value!.Recording);
        Assert.Null(meetingRoom.ActiveEgressId);
        roomRepoMock.Verify(r => r.Update(meetingRoom), Times.Once);
    }

    [Fact]
    public async Task SetRecordingAsync_ReturnsForbidden_WhenCallerIsNotHost()
    {
        var translationRoomId = Guid.NewGuid();
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = Guid.NewGuid(), ProviderRoomName = "room-1" };
        SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

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
    public async Task HandleHostOfflineAsync_ElectsEarliestJoinedActiveParticipant_WhenDepartedUserWasHost()
    {
        var translationRoomId = Guid.NewGuid();
        var meetingRoomId = Guid.NewGuid();
        var departedHostId = Guid.NewGuid();
        var earlierUserId = Guid.NewGuid();
        var laterUserId = Guid.NewGuid();

        var meetingRoom = new MeetingRoom { Id = meetingRoomId, TranslationRoomId = translationRoomId, ActiveHostId = departedHostId, ProviderRoomName = "room-1" };
        var roomRepoMock = SetupMeetingRoomRepository(_unitOfWorkMock, meetingRoom);

        var participants = new List<MeetingParticipant>
        {
            new() { MeetingRoomId = meetingRoomId, UserId = laterUserId, IsActive = true, JoinedAt = DateTime.UtcNow },
            new() { MeetingRoomId = meetingRoomId, UserId = earlierUserId, IsActive = true, JoinedAt = DateTime.UtcNow.AddMinutes(-5) },
        };
        var participantRepoMock = new Mock<IMeetingParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants);
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository).Returns(participantRepoMock.Object);

        var result = await _sut.HandleHostOfflineAsync(translationRoomId, departedHostId);

        Assert.True(result.IsSuccess);
        Assert.Equal(earlierUserId, meetingRoom.ActiveHostId);
        roomRepoMock.Verify(r => r.Update(meetingRoom), Times.Once);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<object>(payload => HasProperty(payload, "Command", "HostChanged") && HasProperty(payload, "NewHostUserId", earlierUserId.ToString()))),
            Times.Once);
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

        var participantRepoMock = new Mock<IMeetingParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MeetingParticipant>());
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository).Returns(participantRepoMock.Object);

        var result = await _sut.HandleHostOfflineAsync(translationRoomId, departedHostId);

        Assert.True(result.IsSuccess);
        Assert.Null(meetingRoom.ActiveHostId);
        roomRepoMock.Verify(r => r.Update(meetingRoom), Times.Once);
        _redisServiceMock.Verify(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
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

        var invitation = new MeetingInvitation
        {
            Id = Guid.NewGuid(),
            MeetingRoomId = meetingRoomId,
            InviteeUserId = userId,
            Status = "DECLINED"
        };
        var invitationRepoMock = new Mock<IGenericRepository<MeetingInvitation>>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingInvitation, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invitation);
        _unitOfWorkMock.Setup(u => u.Repository<MeetingInvitation>()).Returns(invitationRepoMock.Object);

        var result = await _sut.JoinMeetingAsync(translationRoomId, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        _unitOfWorkMock.Verify(u => u.RollbackTransactionAsync(), Times.Once);
    }

    [Fact]
    public async Task EndMeetingAsync_TriggersAiSummary_WithoutUnusedMeetingEndedPubSub()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid().ToString();

        var roomDetails = new WarpTalk.Shared.Protos.GetTranslationRoomResponse
        {
            HostId = hostId.ToString(),
            Status = "IN_PROGRESS",
            WorkspaceId = workspaceId
        };
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(roomDetails));

        var roomRepoMock = new Mock<IMeetingRoomRepository>();
        roomRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingRoom?)null);
        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(roomRepoMock.Object);

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

        _redisServiceMock.Verify(
            r => r.PublishStreamMessageAsync(
                "stt:results",
                It.Is<Dictionary<string, string>>(fields =>
                    fields["meeting_id"] == translationRoomId.ToString() &&
                    fields["text"] == "__MEETING_END__")),
            Times.Once);
    }

    [Fact]
    public async Task EndMeetingAsync_StillSucceeds_WhenAiSummaryTriggerFails()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();

        var roomDetails = new WarpTalk.Shared.Protos.GetTranslationRoomResponse
        {
            HostId = hostId.ToString(),
            Status = "IN_PROGRESS",
            WorkspaceId = Guid.NewGuid().ToString()
        };
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(roomDetails));

        var roomRepoMock = new Mock<IMeetingRoomRepository>();
        roomRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingRoom?)null);
        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(roomRepoMock.Object);

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

        var participantRepoMock = new Mock<IMeetingParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeetingParticipant
            {
                MeetingRoomId = meetingRoom.Id,
                UserId = participantUserId,
                IsActive = true
            });
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository).Returns(participantRepoMock.Object);

        var invitationRepoMock = new Mock<IGenericRepository<MeetingInvitation>>();
        invitationRepoMock
            .Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingInvitation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingInvitation?)null);
        _unitOfWorkMock.Setup(u => u.Repository<MeetingInvitation>()).Returns(invitationRepoMock.Object);

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
}
