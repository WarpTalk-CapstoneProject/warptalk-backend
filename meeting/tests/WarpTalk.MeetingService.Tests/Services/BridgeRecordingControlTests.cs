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
using Xunit;
using RoomResponse = WarpTalk.Shared.Protos.GetTranslationRoomResponse;

namespace WarpTalk.MeetingService.Tests.Services;

/// <summary>
/// WT-910 (PO 2026-10-01): in a Google Meet bridge room only the host or the current capturer may
/// start or stop the recording; a native room keeps "anyone in the meeting".
///
/// The refusals are the tests that matter. Every plain-member case below gives the caller an
/// ACTIVE participant row on purpose — that row is exactly what lets them through the native rule,
/// so a regression that falls back to it in a bridge room fails here and nowhere else.
/// </summary>
public class BridgeRecordingControlTests
{
    private readonly Mock<ITranslationRoomGrpcService> _grpcServiceMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IRedisService> _redisServiceMock = new();
    private readonly Mock<ILiveKitEgressService> _egressServiceMock = new();
    private readonly MeetingRoomService _sut;

    private readonly Guid _translationRoomId = Guid.NewGuid();
    private readonly Guid _hostId = Guid.NewGuid();
    private readonly Guid _capturerId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly MeetingRoom _meetingRoom;

    public BridgeRecordingControlTests()
    {
        _redisServiceMock
            .Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ReturnsAsync(Result.Success());
        _redisServiceMock
            .Setup(r => r.PublishStreamMessageAsync(It.IsAny<string>(), It.IsAny<System.Collections.Generic.Dictionary<string, string>>()))
            .ReturnsAsync(Result.Success());
        _egressServiceMock
            .Setup(e => e.StartRoomCompositeEgressAsync("room-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("egress-1"));
        _egressServiceMock
            .Setup(e => e.StopEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(true));

        _meetingRoom = new MeetingRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = _translationRoomId,
            ProviderRoomName = "room-1",
        };
        var roomRepoMock = new Mock<IMeetingRoomRepository>();
        roomRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_meetingRoom);
        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(roomRepoMock.Object);

        // Whoever the caller is, they have a live participant row: the native rule would admit them.
        var participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RtcStreamParticipant { MeetingRoomId = _meetingRoom.Id, IsActive = true, JoinedAt = DateTime.UtcNow });
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(participantRepoMock.Object);

        _sut = new MeetingRoomService(
            Mock.Of<ILiveKitTokenService>(),
            _grpcServiceMock.Object,
            _unitOfWorkMock.Object,
            _redisServiceMock.Object,
            _egressServiceMock.Object,
            Mock.Of<ILiveKitRoomAdminService>(),
            Mock.Of<ILogger<MeetingRoomService>>());
    }

    private RoomResponse Room(string roomType, Guid? capturer = null) => new()
    {
        Id = _translationRoomId.ToString(),
        HostId = _hostId.ToString(),
        TranslationRoomType = roomType,
        BridgeCapturerUserId = capturer?.ToString() ?? string.Empty,
    };

    private RoomResponse BridgeRoom(Guid? capturer = null) => Room(ExternalBridgeConstants.RoomType, capturer);

    private void SetupCache(RoomResponse? room) =>
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<RoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<RoomResponse?>(room));

    private void SetupGrpc(Result<RoomResponse> result) =>
        _grpcServiceMock.Setup(g => g.GetRoomDetailsAsync(_translationRoomId)).ReturnsAsync(result);

    private void VerifyNoEgressCall()
    {
        _egressServiceMock.Verify(e => e.StartRoomCompositeEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _egressServiceMock.Verify(e => e.StopEgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BridgeHost_CanStop_EvenAfterHandingCaptureToSomeoneElse()
    {
        _meetingRoom.ActiveEgressId = "egress-1";
        SetupCache(BridgeRoom(_capturerId));
        SetupGrpc(Result.Success(BridgeRoom(_capturerId)));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _hostId, "stop");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Recording);
        _egressServiceMock.Verify(e => e.StopEgressAsync("egress-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BridgeHost_CanStart()
    {
        SetupCache(null);
        SetupGrpc(Result.Success(BridgeRoom(_capturerId)));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _hostId, "start");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Recording);
    }

    [Fact]
    public async Task BridgeCapturer_CanStart_WithoutBeingTheHost()
    {
        SetupCache(BridgeRoom(_capturerId));
        SetupGrpc(Result.Success(BridgeRoom(_capturerId)));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _capturerId, "start");

        Assert.True(result.IsSuccess);
        Assert.Equal("egress-1", _meetingRoom.ActiveEgressId);
    }

    [Fact]
    public async Task BridgeCapturer_CanStop()
    {
        _meetingRoom.ActiveEgressId = "egress-1";
        SetupCache(BridgeRoom(_capturerId));
        SetupGrpc(Result.Success(BridgeRoom(_capturerId)));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _capturerId, "stop");

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task BridgePlainMember_IsForbidden_ThoughTheyAreInTheRoom(string action)
    {
        _meetingRoom.ActiveEgressId = action == "stop" ? "egress-1" : null;
        SetupCache(BridgeRoom(_capturerId));
        SetupGrpc(Result.Success(BridgeRoom(_capturerId)));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _memberId, action);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal(MeetingRoomService.BridgeRecordingControlForbiddenMessage, result.Error);
        VerifyNoEgressCall();
    }

    [Fact]
    public async Task BridgePlainMember_IsForbidden_WhenTheRoomIsNotCachedEither()
    {
        _meetingRoom.ActiveEgressId = "egress-1";
        SetupCache(null);
        SetupGrpc(Result.Success(BridgeRoom(_capturerId)));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _memberId, "stop");

        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        VerifyNoEgressCall();
    }

    /// <summary>A takeover moved capture away; the cached projection still names the old capturer.</summary>
    [Fact]
    public async Task FormerCapturer_IsForbidden_WhenOnlyTheStaleCacheStillNamesThem()
    {
        _meetingRoom.ActiveEgressId = "egress-1";
        SetupCache(BridgeRoom(_memberId));
        SetupGrpc(Result.Success(BridgeRoom(_capturerId)));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _memberId, "stop");

        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        VerifyNoEgressCall();
    }

    /// <summary>The capturer cannot be confirmed: refuse, never decide on the cached projection.</summary>
    [Fact]
    public async Task BridgeRoom_IsRefused_WhenTheFreshReadFails()
    {
        SetupCache(BridgeRoom(_capturerId));
        SetupGrpc(Result.Failure<RoomResponse>("down", ErrorCodes.ServiceUnavailable));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _capturerId, "start");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ServiceUnavailable, result.ErrorCode);
        VerifyNoEgressCall();
    }

    [Fact]
    public async Task ActiveHostOfThisService_CountsAsHost_InABridgeRoom()
    {
        _meetingRoom.ActiveHostId = _memberId;
        SetupCache(BridgeRoom(_capturerId));
        SetupGrpc(Result.Success(BridgeRoom(_capturerId)));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _memberId, "start");

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task NativeMember_IsStillAllowed(string action)
    {
        _meetingRoom.ActiveEgressId = action == "stop" ? "egress-1" : null;
        // "Capturer" set on a native room on purpose: the capturer clause must not leak out of a
        // bridge, and the native rule must not start caring about it.
        SetupCache(Room("MEETING", _capturerId));
        SetupGrpc(Result.Success(Room("MEETING", _capturerId)));

        var result = await _sut.SetRecordingAsync(_translationRoomId, _memberId, action);

        Assert.True(result.IsSuccess);
        Assert.Equal(action == "start", result.Value!.Recording);
        // A native press with a warm cache costs no gRPC round-trip, exactly as before WT-910.
        _grpcServiceMock.Verify(g => g.GetRoomDetailsAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void CanControlBridgeRecording_IsHostOrCapturer_AndOnlyInABridgeRoom()
    {
        var bridge = BridgeRoom(_capturerId);
        bridge.EffectiveHostId = _memberId.ToString();

        Assert.True(MeetingRoomService.CanControlBridgeRecording(bridge, null, _hostId));
        Assert.True(MeetingRoomService.CanControlBridgeRecording(bridge, null, _capturerId));
        Assert.True(MeetingRoomService.CanControlBridgeRecording(bridge, null, _memberId)); // effective host
        Assert.False(MeetingRoomService.CanControlBridgeRecording(bridge, null, Guid.NewGuid()));
        Assert.False(MeetingRoomService.CanControlBridgeRecording(Room("MEETING", _capturerId), null, _capturerId));
    }
}
