using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-396 — carrying a speaker's chosen voice to the workers that do the dubbing.
///
/// A person uploaded a recording of themselves, the UI listed the profile as active, and the dub
/// came back in a stock catalogue voice. This broadcast is the ONLY thing the AI workers learn
/// about a room, and the choice was not in it — so tts_worker went on looking for the one voice
/// it knew about, a clone built live from the meeting's microphone.
///
/// These pin the publish side: the choice is attached to the routes the speaker speaks on, it is
/// asked for once per person rather than once per pairing, and every way of not having an answer
/// still publishes a room that works.
/// </summary>
public class DubVoicePublishedTests
{
    private readonly Guid _roomId = Guid.NewGuid();
    private readonly Guid _speakerUserId = Guid.NewGuid();
    private readonly Guid _listenerUserId = Guid.NewGuid();

    private readonly Mock<ITranslationRoomAudioRouteRepository> _routes = new();
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomSessionRepository> _sessions = new();
    private readonly Mock<IRedisStateRepository> _redis = new();
    private readonly Mock<IDubVoiceDirectory> _dubVoices = new();

    private readonly TranslationRoomParticipant _speaker;
    private readonly TranslationRoomParticipant _listener;

    public DubVoicePublishedTests()
    {
        _speaker = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(), TranslationRoomId = _roomId, UserId = _speakerUserId,
            DisplayName = "Speaker", Role = "participant",
            SpeakLanguage = "vi", ListenLanguage = "vi", Status = "CONNECTED", ConnectionType = "web",
        };
        _listener = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(), TranslationRoomId = _roomId, UserId = _listenerUserId,
            DisplayName = "Listener", Role = "participant",
            SpeakLanguage = "en", ListenLanguage = "en", Status = "CONNECTED", ConnectionType = "web",
        };

        _rooms
            .Setup(r => r.GetByIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = _roomId, SourceLanguage = "vi", TargetLanguages = "[\"en\"]", Status = "IN_PROGRESS" });
    }

    private void RoutesAre(params TranslationRoomAudioRoute[] routes) =>
        _routes
            .Setup(r => r.GetRoutesByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(routes.ToList());

    private TranslationRoomAudioRoute Route(TranslationRoomParticipant from, TranslationRoomParticipant to) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = _roomId,
        SourceParticipantId = from.Id,
        TargetParticipantId = to.Id,
        SourceParticipant = from,
        TargetParticipant = to,
        SourceLanguage = from.SpeakLanguage,
        TargetLanguage = to.ListenLanguage,
        Status = AudioRouteStatus.PENDING.ToString(),
    };

    private AudioRouteCacheService Service() => new(
        _routes.Object, _rooms.Object, _sessions.Object, _redis.Object, _dubVoices.Object);

    private List<TranslationRoomAudioRouteDto> Published() =>
        Service().PublishRoutesUpdateAsync(_roomId).GetAwaiter().GetResult();

    // ── the choice travels ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AChosenVoiceIsAttachedToTheRoutesThatSpeakerSpeaksOn()
    {
        RoutesAre(Route(_speaker, _listener));
        _dubVoices
            .Setup(d => d.GetSelectionAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DubVoiceSelection("the-voice-they-picked", null, null));

        var published = Published();

        Assert.All(published, r => Assert.Equal("the-voice-they-picked", r.SourceDubVoiceId));
    }

    [Fact]
    public void EachSpeakerCarriesTheirOwnChoiceAndNobodyElsesa()
    {
        // Both directions of a two-person room. Attaching one person's voice to the other's
        // routes would dub them as each other, which is worse than nobody's choice working.
        RoutesAre(Route(_speaker, _listener), Route(_listener, _speaker));
        _dubVoices.Setup(d => d.GetSelectionAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DubVoiceSelection("speaker-voice", null, null));
        _dubVoices.Setup(d => d.GetSelectionAsync(_listenerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DubVoiceSelection("listener-voice", null, null));

        var published = Published();

        Assert.Equal("speaker-voice",
            published.Single(r => r.SourceUserId == _speakerUserId).SourceDubVoiceId);
        Assert.Equal("listener-voice",
            published.Single(r => r.SourceUserId == _listenerUserId).SourceDubVoiceId);
    }

    // ── and not having one still publishes a working room ────────────────────────────────────

    [Fact]
    public void NoChoiceLeavesTheFieldEmptyRatherThanFailing()
    {
        RoutesAre(Route(_speaker, _listener));
        _dubVoices
            .Setup(d => d.GetSelectionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DubVoiceSelection.None);

        Assert.All(Published(), r => Assert.Null(r.SourceDubVoiceId));
    }

    [Fact]
    public void AGuestHasNoAccountAndIsSimplyNotAsked()
    {
        var guest = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(), TranslationRoomId = _roomId, UserId = null,
            DisplayName = "Guest", Role = "participant",
            SpeakLanguage = "vi", ListenLanguage = "vi", Status = "CONNECTED", ConnectionType = "web",
        };
        RoutesAre(Route(guest, _listener));

        var published = Published();

        Assert.All(published, r => Assert.Null(r.SourceDubVoiceId));
        _dubVoices.Verify(
            d => d.GetSelectionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── cost ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneQuestionPerSpeakerNotOnePerRoute()
    {
        // The mesh is O(n^2) in participants and the answer is a property of the person. Asking
        // per route makes a six-way room spend thirty round trips learning six facts, on the path
        // that starts every meeting.
        var third = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(), TranslationRoomId = _roomId, UserId = Guid.NewGuid(),
            DisplayName = "Third", Role = "participant",
            SpeakLanguage = "ja", ListenLanguage = "ja", Status = "CONNECTED", ConnectionType = "web",
        };
        RoutesAre(Route(_speaker, _listener), Route(_speaker, third));
        _dubVoices
            .Setup(d => d.GetSelectionAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DubVoiceSelection("one-voice", null, null));

        Published();

        _dubVoices.Verify(
            d => d.GetSelectionAsync(_speakerUserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── WT-B: the carried-over clone travels on its OWN field ───────────────────────────────

    [Fact]
    public void ACarriedOverCloneReachesTheWorkersWithItsScore()
    {
        RoutesAre(Route(_speaker, _listener));
        _dubVoices
            .Setup(d => d.GetSelectionAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DubVoiceSelection(null, "voice-from-last-meeting", "0.812"));

        var published = Published();

        Assert.All(published, r =>
        {
            Assert.Equal("voice-from-last-meeting", r.SourceAutoCloneVoiceId);
            Assert.Equal("0.812", r.SourceAutoCloneScore);
        });
    }

    [Fact]
    public void ACarriedCloneIsNeverPublishedAsADeliberatePick()
    {
        // The separation the whole feature rests on. Read as a pick, the worker stops capturing
        // and the speaker is frozen at the first clone they ever earned — which is the state B
        // exists to end.
        RoutesAre(Route(_speaker, _listener));
        _dubVoices
            .Setup(d => d.GetSelectionAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DubVoiceSelection(null, "voice-from-last-meeting", "0.812"));

        var published = Published();

        Assert.All(published, r => Assert.Null(r.SourceDubVoiceId));
    }

    [Fact]
    public void APickAndACarriedCloneTravelTogetherWithoutOverwritingEachOther()
    {
        // Both facts are sent even though only one can win, because the WORKER decides the
        // precedence and it cannot decide what it was never told.
        RoutesAre(Route(_speaker, _listener));
        _dubVoices
            .Setup(d => d.GetSelectionAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DubVoiceSelection("chosen", "carried", "0.5"));

        var published = Published();

        Assert.All(published, r =>
        {
            Assert.Equal("chosen", r.SourceDubVoiceId);
            Assert.Equal("carried", r.SourceAutoCloneVoiceId);
        });
    }

    [Fact]
    public void AnUnmeasuredCarriedCloneTravelsWithNoScoreRatherThanAZero()
    {
        // A zero would reach the worker as "the worst possible sample" and invite replacement by
        // any clip at all. Absent has to stay absent all the way down.
        RoutesAre(Route(_speaker, _listener));
        _dubVoices
            .Setup(d => d.GetSelectionAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DubVoiceSelection(null, "carried", null));

        var published = Published();

        Assert.All(published, r => Assert.Null(r.SourceAutoCloneScore));
    }
}
