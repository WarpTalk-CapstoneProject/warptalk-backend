using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
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
/// S7 — a participant who joins mid-meeting must get audio route rows.
///
/// Routes were generated exactly once, inside StartTranslationRoomAsync. Nothing on the join
/// path added any, and restarting did not help because StartTranslationRoomAsync returns early
/// for a room that is already IN_PROGRESS. Translation and TTS still worked for a late joiner
/// (the AI re-reads the live languages hash per utterance), but
/// BaseWorker.is_voice_clone_consented matches against the route rows delivered by
/// AUDIO_ROUTES_UPDATED — with no row it fails closed, the buffered audio is discarded, and the
/// participant is permanently dubbed in a hashed default voice instead of their own.
/// </summary>
public class LateJoinerAudioRouteTests
{
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _routes = new();
    private readonly Mock<IAudioRouteCacheService> _cache = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly TranslationRoomAudioRouteService _service;

    private readonly Guid _roomId = Guid.NewGuid();

    public LateJoinerAudioRouteTests()
    {
        _uow.Setup(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _uow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participants.Object);
        _uow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_routes.Object);

        var languagePolicy = new Mock<ILanguagePolicy>();
        // The real rule: a pair needs a route when the two languages differ.
        languagePolicy
            .Setup(p => p.IsTranslationRequired(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string source, string target) => !string.Equals(source, target, StringComparison.OrdinalIgnoreCase));

        _service = new TranslationRoomAudioRouteService(
            _uow.Object,
            _cache.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            languagePolicy.Object,
            // Granted by default: this file is about routing a late joiner, not about permission.
            // The gate itself is pinned in VoiceCloneConsentGateTests.
            GrantedConsent(),
            // No preference expressed: WT-401 seeding is pinned in VoiceCloneSeedingTests, and
            // leaving it silent here keeps every assertion below meaning what it meant before.
            NoVoicePreference(),
            Mock.Of<IRedisStateRepository>(),
            NullLogger<TranslationRoomAudioRouteService>.Instance);

        _rooms.Setup(r => r.GetByIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom
            {
                Id = _roomId,
                Status = "IN_PROGRESS",
                SourceLanguage = "en",
                TargetLanguages = """["vi","ja"]""",
            });
    }

    private TranslationRoomParticipant Participant(string speak, string listen) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = _roomId,
        UserId = Guid.NewGuid(),
        DisplayName = "P",
        Role = "participant",
        SpeakLanguage = speak,
        ListenLanguage = listen,
        Status = "CONNECTED",
        ConnectionType = "web",
    };

    private TranslationRoomAudioRoute Route(Guid source, Guid target, string sourceLang, string targetLang, bool clone = false) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = _roomId,
        SourceParticipantId = source,
        TargetParticipantId = target,
        SourceLanguage = sourceLang,
        TargetLanguage = targetLang,
        VoiceCloneEnabled = clone,
        Status = AudioRouteStatus.PENDING.ToString(),
    };

    private List<TranslationRoomAudioRoute> Captured() =>
        _capturedNewRoutes ?? new List<TranslationRoomAudioRoute>();

    private List<TranslationRoomAudioRoute>? _capturedNewRoutes;

    private void ArrangeRoster(List<TranslationRoomParticipant> roster, List<TranslationRoomAudioRoute> existing)
    {
        _participants.Setup(p => p.GetByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(roster);
        _routes.Setup(r => r.GetRoutesByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _routes.Setup(r => r.AddRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TranslationRoomAudioRoute>, CancellationToken>((added, _) => _capturedNewRoutes = added.ToList())
            .Returns(Task.CompletedTask);
    }

    /// <summary>A directory that reports no stored preference — the pre-WT-401 behaviour.</summary>
    private static IUserSettingsDirectory NoVoicePreference()
    {
        var directory = new Mock<IUserSettingsDirectory>();
        directory
            .Setup(d => d.GetVoicePreferenceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserVoicePreference?)null);
        return directory.Object;
    }

    [Fact]
    public async Task LateJoiner_GetsARouteRowInBothDirections()
    {
        var speaker = Participant(speak: "en", listen: "en");
        var joiner = Participant(speak: "vi", listen: "vi");
        ArrangeRoster(new List<TranslationRoomParticipant> { speaker, joiner }, new List<TranslationRoomAudioRoute>());

        var result = await _service.AddRoutesForParticipantAsync(_roomId, joiner.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Captured().Should().HaveCount(2);
        Captured().Should().Contain(r => r.SourceParticipantId == joiner.Id && r.TargetParticipantId == speaker.Id);
        Captured().Should().Contain(r => r.SourceParticipantId == speaker.Id && r.TargetParticipantId == joiner.Id);
    }

    [Fact]
    public async Task LateJoiner_RoutesReachTheAiWorkers()
    {
        // The route rows are useless to the pipeline unless AUDIO_ROUTES_UPDATED carries them:
        // that broadcast is the ONLY thing that populates BaseWorker._room_routes, which the
        // voice-clone consent gate reads.
        var speaker = Participant(speak: "en", listen: "en");
        var joiner = Participant(speak: "vi", listen: "vi");
        ArrangeRoster(new List<TranslationRoomParticipant> { speaker, joiner }, new List<TranslationRoomAudioRoute>());

        await _service.AddRoutesForParticipantAsync(_roomId, joiner.Id, CancellationToken.None);

        _cache.Verify(c => c.PublishRoutesUpdateAsync(_roomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LateJoiner_DoesNotRebuildTheWholeMesh()
    {
        // The thundering-herd guard: with N people already here, joining must add 2*(N-1)
        // routes, not re-evaluate all N*(N+1) pairs.
        var a = Participant(speak: "en", listen: "en");
        var b = Participant(speak: "vi", listen: "vi");
        var c = Participant(speak: "ja", listen: "ja");
        var joiner = Participant(speak: "ko", listen: "ko");

        var existing = new List<TranslationRoomAudioRoute>
        {
            Route(a.Id, b.Id, "en", "vi"), Route(b.Id, a.Id, "vi", "en"),
            Route(a.Id, c.Id, "en", "ja"), Route(c.Id, a.Id, "ja", "en"),
            Route(b.Id, c.Id, "vi", "ja"), Route(c.Id, b.Id, "ja", "vi"),
        };
        ArrangeRoster(new List<TranslationRoomParticipant> { a, b, c, joiner }, existing);

        await _service.AddRoutesForParticipantAsync(_roomId, joiner.Id, CancellationToken.None);

        Captured().Should().HaveCount(6);
        Captured().Should().OnlyContain(r => r.SourceParticipantId == joiner.Id || r.TargetParticipantId == joiner.Id);
        _routes.Verify(r => r.UpdateRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExistingSpeakersConsent_IsCarriedOntoTheirNewRouteToTheJoiner()
    {
        // Consent is per speaker, per meeting — SetVoiceCloneConsentAsync applies it to every
        // route where the caller is the source, precisely because a participant consents once
        // to "my voice may be cloned", not once per listener. Defaulting the new route to false
        // would drop an already-consented speaker back to a hashed default voice the moment
        // somebody walked in late.
        var a = Participant(speak: "en", listen: "en");
        var b = Participant(speak: "vi", listen: "vi");
        var joiner = Participant(speak: "ja", listen: "ja");

        var existing = new List<TranslationRoomAudioRoute>
        {
            Route(a.Id, b.Id, "en", "vi", clone: true),   // a said yes
            Route(b.Id, a.Id, "vi", "en", clone: false),  // b did not
        };
        ArrangeRoster(new List<TranslationRoomParticipant> { a, b, joiner }, existing);

        await _service.AddRoutesForParticipantAsync(_roomId, joiner.Id, CancellationToken.None);

        Captured().Single(r => r.SourceParticipantId == a.Id && r.TargetParticipantId == joiner.Id)
            .VoiceCloneEnabled.Should().BeTrue();
        Captured().Single(r => r.SourceParticipantId == b.Id && r.TargetParticipantId == joiner.Id)
            .VoiceCloneEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task JoinersOwnOutgoingRoutes_StillStartOptedOut()
    {
        // Voice cloning captures biometric data. Inheriting an existing speaker's own answer is
        // one thing; assuming a brand-new participant's is another, and must not happen.
        var a = Participant(speak: "en", listen: "en");
        var joiner = Participant(speak: "vi", listen: "vi");
        ArrangeRoster(
            new List<TranslationRoomParticipant> { a, joiner },
            new List<TranslationRoomAudioRoute> { Route(a.Id, Guid.NewGuid(), "en", "vi", clone: true) });

        await _service.AddRoutesForParticipantAsync(_roomId, joiner.Id, CancellationToken.None);

        Captured().Single(r => r.SourceParticipantId == joiner.Id)
            .VoiceCloneEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SameLanguagePair_NeedsNoRoute()
    {
        var a = Participant(speak: "en", listen: "en");
        var joiner = Participant(speak: "en", listen: "en");
        ArrangeRoster(new List<TranslationRoomParticipant> { a, joiner }, new List<TranslationRoomAudioRoute>());

        var result = await _service.AddRoutesForParticipantAsync(_roomId, joiner.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _routes.Verify(r => r.AddRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.PublishRoutesUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rejoin_RefreshesStaleLanguagesInsteadOfDuplicatingTheRoute()
    {
        var a = Participant(speak: "en", listen: "en");
        var rejoiner = Participant(speak: "ja", listen: "ja");  // used to be vi
        var existing = new List<TranslationRoomAudioRoute>
        {
            Route(rejoiner.Id, a.Id, "vi", "en"),
            Route(a.Id, rejoiner.Id, "en", "vi"),
        };
        ArrangeRoster(new List<TranslationRoomParticipant> { a, rejoiner }, existing);

        List<TranslationRoomAudioRoute>? updated = null;
        _routes.Setup(r => r.UpdateRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TranslationRoomAudioRoute>, CancellationToken>((rows, _) => updated = rows.ToList())
            .Returns(Task.CompletedTask);

        await _service.AddRoutesForParticipantAsync(_roomId, rejoiner.Id, CancellationToken.None);

        _routes.Verify(r => r.AddRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()), Times.Never);
        updated.Should().NotBeNull();
        updated!.Should().HaveCount(2);
        updated.Should().Contain(r => r.SourceParticipantId == rejoiner.Id && r.SourceLanguage == "ja");
        updated.Should().Contain(r => r.TargetParticipantId == rejoiner.Id && r.TargetLanguage == "ja");
    }

    [Fact]
    public async Task UnknownParticipant_IsRejected()
    {
        ArrangeRoster(new List<TranslationRoomParticipant> { Participant("en", "en") }, new List<TranslationRoomAudioRoute>());

        var result = await _service.AddRoutesForParticipantAsync(_roomId, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _routes.Verify(r => r.AddRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IVoiceConsentDirectory GrantedConsent()
    {
        var mock = new Mock<IVoiceConsentDirectory>();
        mock.Setup(d => d.HasVoiceCloneConsentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return mock.Object;
    }
}
