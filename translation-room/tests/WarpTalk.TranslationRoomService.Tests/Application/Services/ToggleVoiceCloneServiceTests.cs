using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// TranslationRoomAudioRouteService.ToggleVoiceCloneAsync — flipping the voice-clone flag on one route.
/// </summary>
public class ToggleVoiceCloneServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _mockRouteRepository = new();
    private readonly Mock<IAudioRouteCacheService> _mockCacheService = new();
    private readonly Mock<ILogger<TranslationRoomAudioRouteService>> _mockLogger = new();
    private readonly TranslationRoomAudioRouteService _service;

    private static readonly Guid RoomId = Guid.NewGuid();

    public ToggleVoiceCloneServiceTests()
    {
        _mockUnitOfWork.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_mockRouteRepository.Object);
        _mockUnitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(Mock.Of<ITranslationRoomParticipantRepository>());

        _service = new TranslationRoomAudioRouteService(
            _mockUnitOfWork.Object,
            _mockCacheService.Object,
            Mock.Of<IAudioRouteEventProcessor>(),
            Mock.Of<ILanguagePolicy>(),
            Mock.Of<IVoiceConsentDirectory>(),
            Mock.Of<IUserSettingsDirectory>(),
            Mock.Of<IRedisStateRepository>(),
            _mockLogger.Object);
    }

    private TranslationRoomAudioRoute RouteExists(
        Guid roomId, bool voiceCloneEnabled, AudioRouteStatus status = AudioRouteStatus.BROADCASTING)
    {
        var route = new TranslationRoomAudioRoute
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            SourceParticipantId = Guid.NewGuid(),
            TargetParticipantId = Guid.NewGuid(),
            SourceLanguage = "vi",
            TargetLanguage = "en",
            VoiceCloneEnabled = voiceCloneEnabled,
            Status = status.ToString(),
        };
        _mockRouteRepository
            .Setup(r => r.GetByIdAsync(route.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(route);
        return route;
    }

    private void VerifyNothingWritten()
    {
        _mockRouteRepository.Verify(r => r.Update(It.IsAny<TranslationRoomAudioRoute>()), Times.Never);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockCacheService.Verify(c => c.PublishRoutesUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // UTCID01
    public async Task ToggleVoiceCloneAsync_ShouldEnableAndPublish_WhenRouteInRoomAndCurrentlyDisabled()
    {
        var route = RouteExists(RoomId, voiceCloneEnabled: false);

        var result = await _service.ToggleVoiceCloneAsync(RoomId, route.Id, new ToggleVoiceCloneDto { VoiceCloneEnabled = true });

        result.IsSuccess.Should().BeTrue();
        result.Value!.VoiceCloneEnabled.Should().BeTrue();
        route.VoiceCloneEnabled.Should().BeTrue();
        _mockRouteRepository.Verify(r => r.Update(route), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockCacheService.Verify(c => c.PublishRoutesUpdateAsync(RoomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact] // UTCID02
    public async Task ToggleVoiceCloneAsync_ShouldReturnNotFound_WhenRouteDoesNotExist()
    {
        var routeId = Guid.NewGuid();
        _mockRouteRepository
            .Setup(r => r.GetByIdAsync(routeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomAudioRoute?)null);

        var result = await _service.ToggleVoiceCloneAsync(RoomId, routeId, new ToggleVoiceCloneDto { VoiceCloneEnabled = true });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(AudioRouteConstants.ErrorRouteNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        VerifyNothingWritten();
    }

    [Fact] // UTCID03
    public async Task ToggleVoiceCloneAsync_ShouldReturnValidationError_WhenRouteBelongsToAnotherRoom()
    {
        var route = RouteExists(Guid.NewGuid(), voiceCloneEnabled: false);

        var result = await _service.ToggleVoiceCloneAsync(RoomId, route.Id, new ToggleVoiceCloneDto { VoiceCloneEnabled = true });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(AudioRouteConstants.ErrorRouteNotBelongToRoom);
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        route.VoiceCloneEnabled.Should().BeFalse();
        VerifyNothingWritten();
    }

    [Fact] // UTCID04
    public async Task ToggleVoiceCloneAsync_ShouldReturnInvalidState_WhenRouteIsCompleted()
    {
        var route = RouteExists(RoomId, voiceCloneEnabled: false, AudioRouteStatus.COMPLETED);

        var result = await _service.ToggleVoiceCloneAsync(RoomId, route.Id, new ToggleVoiceCloneDto { VoiceCloneEnabled = true });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(AudioRouteConstants.ErrorCannotUpdateCompletedRoute);
        result.ErrorCode.Should().Be(ErrorCodes.InvalidState);
        route.VoiceCloneEnabled.Should().BeFalse();
        VerifyNothingWritten();
    }

    [Fact] // UTCID05
    public async Task ToggleVoiceCloneAsync_ShouldDisableAndPublish_WhenCurrentlyEnabled()
    {
        var route = RouteExists(RoomId, voiceCloneEnabled: true);

        var result = await _service.ToggleVoiceCloneAsync(RoomId, route.Id, new ToggleVoiceCloneDto { VoiceCloneEnabled = false });

        result.IsSuccess.Should().BeTrue();
        result.Value!.VoiceCloneEnabled.Should().BeFalse();
        route.VoiceCloneEnabled.Should().BeFalse();
        _mockRouteRepository.Verify(r => r.Update(route), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockCacheService.Verify(c => c.PublishRoutesUpdateAsync(RoomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact] // UTCID06
    public async Task ToggleVoiceCloneAsync_ShouldSucceedWithoutWriting_WhenValueIsUnchanged()
    {
        var route = RouteExists(RoomId, voiceCloneEnabled: true);

        var result = await _service.ToggleVoiceCloneAsync(RoomId, route.Id, new ToggleVoiceCloneDto { VoiceCloneEnabled = true });

        result.IsSuccess.Should().BeTrue();
        result.Value!.VoiceCloneEnabled.Should().BeTrue();
        VerifyNothingWritten();
    }

    [Theory] // UTCID07
    [InlineData("save")]
    [InlineData("publish")]
    public async Task ToggleVoiceCloneAsync_ShouldReturnInternalServerErrorAndLog_WhenSaveOrPublishThrows(string failingStep)
    {
        var route = RouteExists(RoomId, voiceCloneEnabled: false);
        if (failingStep == "save")
        {
            _mockUnitOfWork
                .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("db down"));
        }
        else
        {
            _mockCacheService
                .Setup(c => c.PublishRoutesUpdateAsync(RoomId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("redis down"));
        }

        var result = await _service.ToggleVoiceCloneAsync(RoomId, route.Id, new ToggleVoiceCloneDto { VoiceCloneEnabled = true });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(AudioRouteConstants.ErrorUnexpected);
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
        _mockLogger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString() == $"Error occurred while toggling voice clone for route {route.Id}"),
            It.IsAny<InvalidOperationException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}
