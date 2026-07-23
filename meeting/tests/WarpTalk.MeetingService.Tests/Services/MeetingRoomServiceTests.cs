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
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

public class MeetingRoomServiceTests
{
    private readonly Mock<ILiveKitTokenService> _tokenServiceMock = new();
    private readonly Mock<ITranslationRoomGrpcService> _grpcServiceMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IRedisService> _redisServiceMock = new();
    private readonly MeetingRoomService _sut;

    public MeetingRoomServiceTests()
    {
        _redisServiceMock
            .Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ReturnsAsync(Result.Success());
        _redisServiceMock
            .Setup(r => r.PublishStreamMessageAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(Result.Success());

        _sut = new MeetingRoomService(
            _tokenServiceMock.Object,
            _grpcServiceMock.Object,
            _unitOfWorkMock.Object,
            _redisServiceMock.Object,
            Mock.Of<ILogger<MeetingRoomService>>());
    }

    [Fact]
    public async Task TriggerAiAsync_PublishesTrackPublishedEvent()
    {
        var translationRoomId = Guid.NewGuid();
        var request = new TriggerAiRequest
        {
            ParticipantIdentity = Guid.NewGuid().ToString()
        };

        var result = await _sut.TriggerAiAsync(translationRoomId, request);

        Assert.True(result.IsSuccess);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "meeting.track_published",
                It.Is<object>(payload =>
                    HasProperty(payload, "RoomName", translationRoomId.ToString()) &&
                    HasProperty(payload, "ParticipantIdentity", request.ParticipantIdentity) &&
                    HasProperty(payload, "TrackId", "audio_track_1"))),
            Times.Once);
    }

    [Fact]
    public async Task TriggerAiAsync_ReturnsFailure_WhenPublishFails()
    {
        _redisServiceMock
            .Setup(r => r.PublishEventAsync("meeting.track_published", It.IsAny<object>()))
            .ReturnsAsync(Result.Failure("Redis unavailable", "REDIS_ERROR"));

        var result = await _sut.TriggerAiAsync(Guid.NewGuid(), new TriggerAiRequest
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
    public async Task EndMeetingAsync_PublishesMeetingEndedEvent_AndTriggersAiSummary()
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
            r => r.PublishEventAsync(
                "meeting.ended",
                It.Is<object>(payload =>
                    HasProperty(payload, "TranslationRoomId", translationRoomId.ToString()) &&
                    HasProperty(payload, "WorkspaceId", workspaceId))),
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

    private static bool HasProperty(object payload, string propertyName, string expectedValue)
    {
        var property = payload.GetType().GetProperty(propertyName);
        return string.Equals(property?.GetValue(payload)?.ToString(), expectedValue, StringComparison.Ordinal);
    }
}
