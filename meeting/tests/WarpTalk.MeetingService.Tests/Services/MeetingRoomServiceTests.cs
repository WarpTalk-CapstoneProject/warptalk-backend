using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
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

    private static bool HasProperty(object payload, string propertyName, string expectedValue)
    {
        var property = payload.GetType().GetProperty(propertyName);
        return string.Equals(property?.GetValue(payload)?.ToString(), expectedValue, StringComparison.Ordinal);
    }
}
