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

/// <summary>
/// The gate in front of biometric processing.
///
/// voice_clone_enabled on an audio route is the flag the AI pipeline reads before it will build a
/// voice model from somebody's speech (base_worker.is_voice_clone_consented). Until now anyone in
/// a room could set it for themselves and nothing outside that meeting recorded the decision.
/// These pin that it can now only be turned ON for someone AuthService holds a live consent
/// record for — and, just as importantly, that it can always be turned OFF.
/// </summary>
public class VoiceCloneConsentGateTests
{
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _routes = new();
    private readonly Mock<IVoiceConsentDirectory> _consent = new();
    private readonly TranslationRoomAudioRouteService _service;

    private readonly Guid _roomId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TranslationRoomParticipant _participant;
    private readonly TranslationRoomAudioRoute _route;

    public VoiceCloneConsentGateTests()
    {
        _participant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = _roomId,
            UserId = _userId,
            DisplayName = "Me",
            Role = "participant",
            ListenLanguage = "en",
            SpeakLanguage = "vi",
            Status = "CONNECTED",
            ConnectionType = "web",
        };

        _route = new TranslationRoomAudioRoute
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = _roomId,
            SourceParticipantId = _participant.Id,
            TargetParticipantId = Guid.NewGuid(),
            SourceLanguage = "vi",
            TargetLanguage = "en",
            Status = AudioRouteStatus.PENDING.ToString(),
            VoiceCloneEnabled = false,
        };

        _participants
            .Setup(r => r.GetByRoomAndUserAsync(_roomId, _userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_participant);
        _routes
            .Setup(r => r.GetRoutesByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute> { _route });

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participants.Object);
        uow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_routes.Object);

        _service = new TranslationRoomAudioRouteService(
            uow.Object,
            new Mock<IAudioRouteCacheService>().Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            new Mock<ILanguagePolicy>().Object,
            _consent.Object,
            NullLogger<TranslationRoomAudioRouteService>.Instance);
    }

    private void ConsentIs(bool granted) =>
        _consent
            .Setup(d => d.HasVoiceCloneConsentAsync(_userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(granted);

    [Fact]
    public async Task EnablingWithoutConsentIsRefused()
    {
        ConsentIs(false);

        var result = await _service.SetVoiceCloneConsentAsync(_roomId, _userId, enabled: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        // The flag is what the AI pipeline reads. If it were written before the refusal was
        // returned, the refusal would be cosmetic and the cloning would go ahead anyway.
        _route.VoiceCloneEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task EnablingWithConsentIsAllowed()
    {
        ConsentIs(true);

        var result = await _service.SetVoiceCloneConsentAsync(_roomId, _userId, enabled: true);

        result.IsSuccess.Should().BeTrue();
        _route.VoiceCloneEnabled.Should().BeTrue();
    }

    /// <summary>
    /// The asymmetry that makes this a consent system rather than a lock. Withdrawal must work
    /// even when the record cannot be read — otherwise a directory outage would leave somebody
    /// unable to stop their own voice being cloned, which is the opposite of what the record is
    /// for.
    /// </summary>
    [Fact]
    public async Task DisablingNeverNeedsConsent()
    {
        _route.VoiceCloneEnabled = true;
        ConsentIs(false);

        var result = await _service.SetVoiceCloneConsentAsync(_roomId, _userId, enabled: false);

        result.IsSuccess.Should().BeTrue();
        _route.VoiceCloneEnabled.Should().BeFalse();
        // Not even asked: turning it off is unconditional.
        _consent.Verify(
            d => d.HasVoiceCloneConsentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
