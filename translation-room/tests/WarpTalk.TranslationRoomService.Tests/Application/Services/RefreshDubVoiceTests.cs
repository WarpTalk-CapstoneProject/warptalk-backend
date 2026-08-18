using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// Making a dub-voice change reach the meeting the person is standing in.
///
/// The voice somebody is dubbed in is a user setting in AuthService, which knows nothing about
/// rooms. This service learns it only while building a route payload
/// (AudioRouteCacheService.WithDubVoicesAsync asks over gRPC on every publish), and the AI
/// workers learn it only from that payload. So a change made mid-meeting was correct in
/// AuthService, correct on the voice-profiles page, and invisible to the meeting until somebody
/// joined or translation was restarted and a publish happened for some unrelated reason.
/// </summary>
public class RefreshDubVoiceTests
{
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _routes = new();
    private readonly Mock<IAudioRouteCacheService> _cache = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly TranslationRoomAudioRouteService _service;

    private readonly Guid _roomId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TranslationRoomParticipant _me;

    public RefreshDubVoiceTests()
    {
        _uow.Setup(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _uow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participants.Object);
        _uow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_routes.Object);

        _me = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = _roomId,
            UserId = _userId,
            DisplayName = "Me",
            Role = "participant",
            SpeakLanguage = "vi",
            ListenLanguage = "vi",
            Status = "CONNECTED",
            ConnectionType = "web",
        };

        var languagePolicy = new Mock<ILanguagePolicy>();
        var consent = new Mock<IVoiceConsentDirectory>();
        var settings = new Mock<IUserSettingsDirectory>();

        _service = new TranslationRoomAudioRouteService(
            _uow.Object,
            _cache.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            languagePolicy.Object,
            consent.Object,
            settings.Object,
            NullLogger<TranslationRoomAudioRouteService>.Instance);
    }

    private void ImInTheRoom() =>
        _participants
            .Setup(p => p.GetByRoomAndUserAsync(_roomId, _userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_me);

    private void RoomHasRoutes(params TranslationRoomAudioRoute[] routes) =>
        _routes
            .Setup(r => r.GetRoutesByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>(routes));

    private TranslationRoomAudioRoute MyOutgoingRoute(string status = "ACTIVE") =>
        new()
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = _roomId,
            SourceParticipantId = _me.Id,
            TargetParticipantId = Guid.NewGuid(),
            SourceLanguage = "vi",
            TargetLanguage = "en",
            Status = status,
        };

    [Fact]
    public async Task Republishes_so_the_workers_re_read_the_choice()
    {
        // The one assertion that matters. Nothing is written here — PublishRoutesUpdateAsync
        // re-reads every speaker's voice from AuthService as it builds the payload, so
        // republishing IS the refresh.
        ImInTheRoom();
        RoomHasRoutes(MyOutgoingRoute());

        var result = await _service.RefreshDubVoiceAsync(_roomId, _userId);

        result.IsSuccess.Should().BeTrue();
        _cache.Verify(c => c.PublishRoutesUpdateAsync(_roomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Somebody_who_is_not_in_the_room_cannot_trigger_a_republish()
    {
        // This publishes a snapshot of the WHOLE room and makes one gRPC call per distinct
        // speaker on the way, so it is not a read a stranger may set off.
        _participants
            .Setup(p => p.GetByRoomAndUserAsync(_roomId, _userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        var result = await _service.RefreshDubVoiceAsync(_roomId, _userId);

        result.IsSuccess.Should().BeFalse();
        _cache.Verify(
            c => c.PublishRoutesUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Returns_only_my_own_outgoing_routes()
    {
        ImInTheRoom();
        var mine = MyOutgoingRoute();
        var somebodyElses = new TranslationRoomAudioRoute
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = _roomId,
            SourceParticipantId = Guid.NewGuid(),
            TargetParticipantId = _me.Id,
            SourceLanguage = "en",
            TargetLanguage = "vi",
            Status = "ACTIVE",
        };
        RoomHasRoutes(mine, somebodyElses);

        var result = await _service.RefreshDubVoiceAsync(_roomId, _userId);

        result.Value.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    [Fact]
    public async Task Leaves_out_routes_that_are_already_finished()
    {
        ImInTheRoom();
        RoomHasRoutes(MyOutgoingRoute(AudioRouteStatus.COMPLETED.ToString()));

        var result = await _service.RefreshDubVoiceAsync(_roomId, _userId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task A_speaker_with_no_listener_yet_still_gets_a_successful_republish()
    {
        // Somebody may set their voice before anybody is listening in another language, so
        // having no outgoing route is an ordinary state — not a reason to refuse.
        ImInTheRoom();
        RoomHasRoutes();

        var result = await _service.RefreshDubVoiceAsync(_roomId, _userId);

        result.IsSuccess.Should().BeTrue();
        _cache.Verify(c => c.PublishRoutesUpdateAsync(_roomId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
