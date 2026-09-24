using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// WT-699 / TC2402 and TC2103.
///
/// TC2402: Reject revoked only this service's session grant and never reached the room service's
/// lobby row, so the knock stayed WAITING — listed and admittable — and before the call had started
/// it failed outright with "Meeting room not started." It now reaches the roster first and works
/// with or without a meeting room.
///
/// TC2103: a second Kick for somebody already removed answered 200 again. It now answers a distinct
/// Conflict, while still (idempotently) finishing any eviction a first kick left undone.
/// </summary>
public class LobbyRejectAndRepeatKickTests
{
    private readonly Mock<ITranslationRoomGrpcService> _grpc = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRedisService> _redis = new();
    private readonly Mock<ILiveKitRoomAdminService> _roomAdmin = new();
    private readonly Mock<IMeetingRoomRepository> _meetingRooms = new();
    private readonly Mock<IRtcStreamParticipantRepository> _participants = new();
    private readonly Mock<IRtcSessionRevocationRepository> _revocations = new();
    private readonly MeetingRoomService _sut;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid GuestId = Guid.NewGuid();

    public LobbyRejectAndRepeatKickTests()
    {
        _unitOfWork.Setup(u => u.MeetingRoomRepository).Returns(_meetingRooms.Object);
        _unitOfWork.Setup(u => u.RtcStreamParticipantRepository).Returns(_participants.Object);
        _unitOfWork.Setup(u => u.RtcSessionRevocationRepository).Returns(_revocations.Object);

        _redis
            .Setup(r => r.GetCacheAsync<GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<GetTranslationRoomResponse?>(new GetTranslationRoomResponse
            {
                Id = RoomId.ToString(),
                HostId = HostId.ToString(),
                WorkspaceId = Guid.NewGuid().ToString()
            }));
        _roomAdmin
            .Setup(r => r.RemoveParticipantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));

        _sut = new MeetingRoomService(
            Mock.Of<ILiveKitTokenService>(),
            _grpc.Object,
            _unitOfWork.Object,
            _redis.Object,
            Mock.Of<ILiveKitEgressService>(),
            _roomAdmin.Object,
            Mock.Of<ILogger<MeetingRoomService>>());
    }

    private void MeetingRoom(MeetingRoom? room) =>
        _meetingRooms
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

    private static MeetingRoom LiveRoom() => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = RoomId,
        ActiveHostId = HostId,
        ProviderRoomName = "provider-room"
    };

    // ── TC2402 ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_BeforeTheCallHasStarted_ReachesTheLobbyRowAndSucceeds()
    {
        MeetingRoom(null);
        _grpc.Setup(g => g.RejectRoomParticipantAsync(RoomId, HostId, GuestId))
            .ReturnsAsync(Result.Success(RoomRosterRemoval.Removed));

        var result = await _sut.RejectParticipantAsync(RoomId, HostId, GuestId);

        Assert.True(result.IsSuccess);
        _grpc.Verify(g => g.RejectRoomParticipantAsync(RoomId, HostId, GuestId), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reject_DuringTheCall_ReachesTheLobbyRowAndRevokesTheSessionGrant()
    {
        var room = LiveRoom();
        MeetingRoom(room);
        _grpc.Setup(g => g.RejectRoomParticipantAsync(RoomId, HostId, GuestId))
            .ReturnsAsync(Result.Success(RoomRosterRemoval.Removed));
        RtcSessionRevocation? added = null;
        _revocations
            .Setup(r => r.AddAsync(It.IsAny<RtcSessionRevocation>(), It.IsAny<CancellationToken>()))
            .Callback<RtcSessionRevocation, CancellationToken>((revocation, _) => added = revocation)
            .Returns(Task.CompletedTask);

        var result = await _sut.RejectParticipantAsync(RoomId, HostId, GuestId);

        Assert.True(result.IsSuccess);
        _grpc.Verify(g => g.RejectRoomParticipantAsync(RoomId, HostId, GuestId), Times.Once);
        Assert.NotNull(added);
        Assert.Equal("REVOKED", added!.Status);
        Assert.Equal(GuestId, added.InviteeUserId);
    }

    /// <summary>A refusal on the room service leaves nothing half-applied here.</summary>
    [Fact]
    public async Task Reject_RefusedByTheRoomService_WritesNothingLocally()
    {
        MeetingRoom(LiveRoom());
        _grpc.Setup(g => g.RejectRoomParticipantAsync(RoomId, HostId, GuestId))
            .ReturnsAsync(Result.Failure<RoomRosterRemoval>("This person is not waiting to join.", "REJECT_REFUSED"));

        var result = await _sut.RejectParticipantAsync(RoomId, HostId, GuestId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reject_ByANonHost_IsForbiddenAndNeverReachesTheRoomService()
    {
        MeetingRoom(LiveRoom());

        var result = await _sut.RejectParticipantAsync(RoomId, Guid.NewGuid(), GuestId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        _grpc.Verify(g => g.RejectRoomParticipantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    // ── TC2103 ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kick_OfSomebodyAlreadyKicked_IsADistinctConflict()
    {
        MeetingRoom(LiveRoom());
        _grpc.Setup(g => g.KickRoomParticipantAsync(RoomId, HostId, GuestId))
            .ReturnsAsync(Result.Success(RoomRosterRemoval.AlreadyRemoved));

        var result = await _sut.KickParticipantAsync(RoomId, HostId, GuestId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
        Assert.Equal(MeetingRoomService.ParticipantAlreadyKickedMessage, result.Error);
        // Still idempotently finishes the eviction a first kick may have left undone.
        _roomAdmin.Verify(
            r => r.RemoveParticipantAsync("provider-room", GuestId.ToString(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Kick_OfSomebodyAlreadyKicked_IsStillAConflict_WhenLiveKitNoLongerKnowsThem()
    {
        MeetingRoom(LiveRoom());
        _grpc.Setup(g => g.KickRoomParticipantAsync(RoomId, HostId, GuestId))
            .ReturnsAsync(Result.Success(RoomRosterRemoval.AlreadyRemoved));
        _roomAdmin
            .Setup(r => r.RemoveParticipantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<bool>("participant not found", ErrorCodes.NotFound));

        var result = await _sut.KickParticipantAsync(RoomId, HostId, GuestId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
    }

    [Fact]
    public async Task Kick_TheFirstTime_StillSucceeds()
    {
        MeetingRoom(LiveRoom());
        _grpc.Setup(g => g.KickRoomParticipantAsync(RoomId, HostId, GuestId))
            .ReturnsAsync(Result.Success(RoomRosterRemoval.Removed));

        var result = await _sut.KickParticipantAsync(RoomId, HostId, GuestId);

        Assert.True(result.IsSuccess);
    }
}
