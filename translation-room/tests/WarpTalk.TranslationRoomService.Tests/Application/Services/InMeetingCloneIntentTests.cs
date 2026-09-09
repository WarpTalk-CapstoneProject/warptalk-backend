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
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-528 — "My voice" pressed in a meeting has to reach the routes it is meant to govern.
///
/// WHAT WAS BROKEN
///     SetVoiceCloneConsentAsync wrote the answer onto the caller's EXISTING routes and nowhere
///     else. Routes are created when translation starts, so somebody who pressed "My voice"
///     beforehand had nothing to write to: the changed-routes list came back empty, the save was
///     skipped, and the method returned Success over a choice it had just discarded.
///
///     The routes created a moment later were then seeded from the ACCOUNT preference
///     (auth.user_settings.voice_clone_enabled), which the in-meeting switch does not write. So
///     the in-meeting answer had no path at all into the routes it was aimed at, and the user had
///     to press the same button again with nothing telling them so.
///
///     Production, 18 Aug, room 01a01542: three participants pressed it while translation was off
///     and got `no_routes` — the UI's "Translation is not running" — then once translation was
///     running all four read `not_opted_in` for the rest of the evening.
///
/// WHAT MUST NOT CHANGE
///     The legal gate. An in-room answer is a PREFERENCE ("use my voice here"), never permission
///     to process biometric data. Without a live consent grant the route stays off however loudly
///     the room key says otherwise — that is the last test in this file and it is the one that
///     matters most.
/// </summary>
public class InMeetingCloneIntentTests
{
    private readonly Guid _roomId = Guid.NewGuid();
    private readonly Guid _speakerUserId = Guid.NewGuid();

    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _routes = new();
    private readonly Mock<IVoiceConsentDirectory> _consent = new();
    private readonly Mock<IUserSettingsDirectory> _settings = new();
    private readonly Mock<IRedisStateRepository> _redis = new();

    private readonly TranslationRoomParticipant _speaker;
    private readonly TranslationRoomParticipant _listener;
    private List<TranslationRoomAudioRoute> _existing = new();
    private readonly List<TranslationRoomAudioRoute> _added = new();

    public InMeetingCloneIntentTests()
    {
        _speaker = Participant(_speakerUserId, "vi", "en");
        _listener = Participant(Guid.NewGuid(), "en", "en");

        _rooms
            .Setup(r => r.GetByIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom
            {
                Id = _roomId,
                SourceLanguage = "vi",
                TargetLanguages = """["en"]""",
            });
        _participants
            .Setup(r => r.GetByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant> { _speaker, _listener });
        _participants
            .Setup(r => r.GetByRoomAndUserAsync(_roomId, _speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_speaker);
        _routes
            .Setup(r => r.GetRoutesByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _existing);
        _routes
            .Setup(r => r.AddRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TranslationRoomAudioRoute>, CancellationToken>((rs, _) => _added.AddRange(rs))
            .Returns(Task.CompletedTask);
    }

    private TranslationRoomParticipant Participant(Guid? userId, string speak, string listen) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = _roomId,
        UserId = userId,
        DisplayName = "Somebody",
        Role = "participant",
        SpeakLanguage = speak,
        ListenLanguage = listen,
        Status = "CONNECTED",
        ConnectionType = "web",
    };

    private TranslationRoomAudioRouteService Service()
    {
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        uow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participants.Object);
        uow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_routes.Object);

        var policy = new Mock<ILanguagePolicy>();
        policy.Setup(p => p.IsTranslationRequired(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        return new TranslationRoomAudioRouteService(
            uow.Object,
            new Mock<IAudioRouteCacheService>().Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            policy.Object,
            _consent.Object,
            _settings.Object,
            _redis.Object,
            NullLogger<TranslationRoomAudioRouteService>.Instance);
    }

    private void Consent(bool granted) =>
        _consent
            .Setup(d => d.HasVoiceCloneConsentAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(granted);

    private void AccountPreference(bool enabled) =>
        _settings
            .Setup(d => d.GetVoicePreferenceAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserVoicePreference(enabled));

    /// <param name="stored">"1", "0", or null for "they never touched the switch in this room".</param>
    private void InRoomAnswer(string? stored) =>
        _redis
            .Setup(r => r.HashGetAsync(
                $"translationRoom:{_roomId}:clone_intent", _speakerUserId.ToString()))
            .ReturnsAsync(stored);

    private bool SeededForSpeaker() =>
        _added.Any(r => r.SourceParticipantId == _speaker.Id)
        && _added.Where(r => r.SourceParticipantId == _speaker.Id).All(r => r.VoiceCloneEnabled);

    // ── the reported bug ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PressingMyVoiceWithNoRoutesYetStillRecordsTheChoice()
    {
        // Translation has not started, so there is nothing to write the flag onto. This is the
        // exact state three people were in at 21:25 and the reason their answer vanished.
        _existing = new List<TranslationRoomAudioRoute>();
        Consent(true);

        var result = await Service().SetVoiceCloneConsentAsync(_roomId, _speakerUserId, true);

        Assert.True(result.IsSuccess);
        _redis.Verify(
            r => r.HashSetAsync(
                $"translationRoom:{_roomId}:clone_intent",
                It.Is<Dictionary<string, string>>(f => f[_speakerUserId.ToString()] == "1")),
            Times.Once);
    }

    [Fact]
    public async Task AChoiceMadeBeforeTranslationStartedIsHonouredWhenRoutesAppear()
    {
        // The whole point: press it early, start translation, be dubbed in your own voice —
        // without pressing it a second time.
        Consent(true);
        AccountPreference(false);
        InRoomAnswer("1");

        await Service().GenerateRoutesAsync(_roomId);

        Assert.True(SeededForSpeaker());
    }

    // ── which answer wins ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TurningItOffInThisRoomBeatsAnAccountPreferenceThatSaysOn()
    {
        // Otherwise a deliberate "not in this meeting" would be undone by a setting they last
        // looked at weeks ago.
        Consent(true);
        AccountPreference(true);
        InRoomAnswer("0");

        await Service().GenerateRoutesAsync(_roomId);

        Assert.False(SeededForSpeaker());
    }

    [Fact]
    public async Task SayingNothingInThisRoomFallsBackToTheAccountPreference()
    {
        Consent(true);
        AccountPreference(true);
        InRoomAnswer(null);

        await Service().GenerateRoutesAsync(_roomId);

        Assert.True(SeededForSpeaker());
    }

    [Fact]
    public async Task AnUnreadableRoomKeyFallsBackRatherThanReadingAsOff()
    {
        // Redis being unavailable must not silently revoke a preference. Falling back is the
        // behaviour that existed before this key, and the consent gate below still applies.
        Consent(true);
        AccountPreference(true);
        _redis
            .Setup(r => r.HashGetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        await Service().GenerateRoutesAsync(_roomId);

        Assert.True(SeededForSpeaker());
    }

    // ── the gate that must hold whatever the room says ───────────────────────────────────────

    [Fact]
    public async Task WithoutConsentTheRoomKeyCannotTurnCloningOn()
    {
        // A room answer is a preference, never permission to process biometric data.
        Consent(false);
        AccountPreference(true);
        InRoomAnswer("1");

        await Service().GenerateRoutesAsync(_roomId);

        Assert.False(SeededForSpeaker());
    }

    [Fact]
    public async Task TurningItOnStillRequiresConsentAtTheEndpoint()
    {
        _existing = new List<TranslationRoomAudioRoute>();
        Consent(false);

        var result = await Service().SetVoiceCloneConsentAsync(_roomId, _speakerUserId, true);

        Assert.False(result.IsSuccess);
        _redis.Verify(
            r => r.HashSetAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task TurningItOffIsRecordedEvenWhenConsentCannotBeRead()
    {
        // Withdrawal must always work: the failure mode of a consent system has to be "less
        // processing", never "you are stuck consenting".
        _existing = new List<TranslationRoomAudioRoute>();
        _consent
            .Setup(d => d.HasVoiceCloneConsentAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("auth down"));

        var result = await Service().SetVoiceCloneConsentAsync(_roomId, _speakerUserId, false);

        Assert.True(result.IsSuccess);
        _redis.Verify(
            r => r.HashSetAsync(
                It.IsAny<string>(),
                It.Is<Dictionary<string, string>>(f => f[_speakerUserId.ToString()] == "0")),
            Times.Once);
    }
}
