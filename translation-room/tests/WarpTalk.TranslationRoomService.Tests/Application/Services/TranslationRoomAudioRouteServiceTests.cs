using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public class TranslationRoomAudioRouteServiceTests
{
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepository;
    private readonly Mock<ITranslationRoomAudioRouteRepository> _mockRouteRepository;
    private readonly Mock<IAudioRouteCacheService> _mockCacheService;
    private readonly TranslationRoomAudioRouteService _service;

    public TranslationRoomAudioRouteServiceTests()
    {
        _mockParticipantRepository = new Mock<ITranslationRoomParticipantRepository>();
        _mockRouteRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        _mockCacheService = new Mock<IAudioRouteCacheService>();
        var mockEventProcessor = new Mock<IAudioRouteEventProcessor>();
        var mockLanguagePolicy = new Mock<ILanguagePolicy>();

        var mockUnitOfWork = new Mock<IUnitOfWork>();
        mockUnitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepository.Object);
        mockUnitOfWork.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_mockRouteRepository.Object);

        _service = new TranslationRoomAudioRouteService(
            mockUnitOfWork.Object,
            _mockCacheService.Object,
            mockEventProcessor.Object,
            mockLanguagePolicy.Object,
            NullLogger<TranslationRoomAudioRouteService>.Instance);
    }

    private static TranslationRoomParticipant MakeParticipant(Guid roomId, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = roomId,
        UserId = userId,
        DisplayName = "Me",
        Role = "participant",
        ListenLanguage = "en",
        SpeakLanguage = "vi",
        Status = "CONNECTED",
        ConnectionType = "web",
    };

    private static TranslationRoomAudioRoute MakeRoute(
        Guid roomId, Guid sourceParticipantId, Guid targetParticipantId,
        bool voiceCloneEnabled = false, AudioRouteStatus status = AudioRouteStatus.PENDING) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = roomId,
        SourceParticipantId = sourceParticipantId,
        TargetParticipantId = targetParticipantId,
        SourceLanguage = "vi",
        TargetLanguage = "en",
        VoiceCloneEnabled = voiceCloneEnabled,
        Status = status.ToString(),
    };

    [Fact]
    public async Task SetVoiceCloneConsentAsync_ShouldEnableOnlyCallersOutgoingRoutes()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participant = MakeParticipant(roomId, userId);
        var otherParticipantId = Guid.NewGuid();

        var myOutgoingRoute = MakeRoute(roomId, participant.Id, otherParticipantId);
        // A route where I'm the LISTENER, not the speaker — my consent must never touch it.
        var someoneElsesRoute = MakeRoute(roomId, otherParticipantId, participant.Id);

        _mockParticipantRepository.Setup(r => r.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        _mockRouteRepository.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute> { myOutgoingRoute, someoneElsesRoute });

        var result = await _service.SetVoiceCloneConsentAsync(roomId, userId, true, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        myOutgoingRoute.VoiceCloneEnabled.Should().BeTrue();
        someoneElsesRoute.VoiceCloneEnabled.Should().BeFalse();

        _mockRouteRepository.Verify(r => r.UpdateRoutesAsync(
            It.Is<IEnumerable<TranslationRoomAudioRoute>>(routes => Contains(routes, myOutgoingRoute.Id) && Count(routes) == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockCacheService.Verify(c => c.PublishRoutesUpdateAsync(roomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetVoiceCloneConsentAsync_ShouldReturnNotFound_WhenCallerNotAParticipant()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _mockParticipantRepository.Setup(r => r.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        var result = await _service.SetVoiceCloneConsentAsync(roomId, userId, true, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        _mockRouteRepository.Verify(
            r => r.UpdateRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetVoiceCloneConsentAsync_ShouldBeNoOp_WhenAlreadyAtDesiredValue()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participant = MakeParticipant(roomId, userId);
        var route = MakeRoute(roomId, participant.Id, Guid.NewGuid(), voiceCloneEnabled: true);

        _mockParticipantRepository.Setup(r => r.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        _mockRouteRepository.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute> { route });

        var result = await _service.SetVoiceCloneConsentAsync(roomId, userId, true, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _mockRouteRepository.Verify(
            r => r.UpdateRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCacheService.Verify(c => c.PublishRoutesUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetVoiceCloneConsentAsync_ShouldExcludeCompletedRoutes()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participant = MakeParticipant(roomId, userId);
        var completedRoute = MakeRoute(roomId, participant.Id, Guid.NewGuid(), status: AudioRouteStatus.COMPLETED);

        _mockParticipantRepository.Setup(r => r.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        _mockRouteRepository.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute> { completedRoute });

        var result = await _service.SetVoiceCloneConsentAsync(roomId, userId, true, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        completedRoute.VoiceCloneEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetVoiceCloneConsentAsync_ShouldDisableConsent_WhenWithdrawn()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participant = MakeParticipant(roomId, userId);
        var route = MakeRoute(roomId, participant.Id, Guid.NewGuid(), voiceCloneEnabled: true);

        _mockParticipantRepository.Setup(r => r.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        _mockRouteRepository.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute> { route });

        var result = await _service.SetVoiceCloneConsentAsync(roomId, userId, false, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        route.VoiceCloneEnabled.Should().BeFalse();
        _mockCacheService.Verify(c => c.PublishRoutesUpdateAsync(roomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static bool Contains(IEnumerable<TranslationRoomAudioRoute> routes, Guid id)
    {
        foreach (var route in routes)
        {
            if (route.Id == id) return true;
        }
        return false;
    }

    private static int Count(IEnumerable<TranslationRoomAudioRoute> routes)
    {
        var count = 0;
        foreach (var _ in routes) count++;
        return count;
    }
}
