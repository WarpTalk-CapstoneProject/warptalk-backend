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
/// WT-401 — whether the "Enable Voice Cloning" switch in Settings reaches a meeting.
///
/// It did not. The switch wrote to AuthService and nothing read it back; every new audio route
/// was created with VoiceCloneEnabled hard-coded false, and the only thing that could change it
/// was a row buried seven deep in the in-meeting gear popover, which reset on every join. A
/// tester who had deliberately turned cloning ON heard a stock catalogue voice and reported that
/// the voice cloning sounded like dubbing — correctly, because it was: the clone had never run.
///
/// These pin the seeding and, more importantly, the four ways it must NOT fire. This is
/// biometric data; the failure that matters is cloning somebody who did not ask.
/// </summary>
public class VoiceCloneSeedingTests
{
    private readonly Guid _roomId = Guid.NewGuid();
    private readonly Guid _speakerUserId = Guid.NewGuid();

    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _routes = new();
    private readonly Mock<IVoiceConsentDirectory> _consent = new();
    private readonly Mock<IUserSettingsDirectory> _settings = new();

    private readonly TranslationRoomParticipant _speaker;
    private readonly TranslationRoomParticipant _listener;
    private List<TranslationRoomAudioRoute> _existing = new();
    private List<TranslationRoomAudioRoute> _added = new();

    public VoiceCloneSeedingTests()
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
            Mock.Of<IRedisStateRepository>(),
            NullLogger<TranslationRoomAudioRouteService>.Instance);
    }

    private void Consent(Guid userId, bool granted) =>
        _consent
            .Setup(d => d.HasVoiceCloneConsentAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(granted);

    private void Preference(Guid userId, bool? enabled) =>
        _settings
            .Setup(d => d.GetVoicePreferenceAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(enabled is null ? null : new UserVoicePreference(enabled.Value));

    private bool SeededForSpeaker() =>
        _added.Where(r => r.SourceParticipantId == _speaker.Id).All(r => r.VoiceCloneEnabled)
        && _added.Any(r => r.SourceParticipantId == _speaker.Id);

    // ── the point of the ticket ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSettingsSwitchNowReachesTheMeeting()
    {
        Consent(_speakerUserId, true);
        Preference(_speakerUserId, true);

        await Service().GenerateRoutesAsync(_roomId);

        Assert.True(SeededForSpeaker(),
            "the user asked to be dubbed in their own voice and the route was still created off");
    }

    // ── the four ways it must not fire ───────────────────────────────────────────────────────

    [Fact]
    public async Task APreferenceWithoutBiometricConsentClonesNobody()
    {
        // The gate that matters. Wanting it is not the same as being allowed, and the legal
        // record of the second lives in AuthService, not in a preferences form.
        Consent(_speakerUserId, false);
        Preference(_speakerUserId, true);

        await Service().GenerateRoutesAsync(_roomId);

        Assert.DoesNotContain(_added, r => r.VoiceCloneEnabled);
    }

    [Fact]
    public async Task ConsentAloneIsNotARequestToBeCloned()
    {
        // Granting permission for a voice profile is not the same as asking for every meeting to
        // use it. Seeding on consent alone would clone people who only ever uploaded a sample.
        Consent(_speakerUserId, true);
        Preference(_speakerUserId, false);

        await Service().GenerateRoutesAsync(_roomId);

        Assert.DoesNotContain(_added, r => r.VoiceCloneEnabled);
    }

    [Fact]
    public async Task AnUnreachableAuthServiceLeavesCloningOff()
    {
        // A preference lookup that fails must not fail the room, and must not open the gate.
        // Route generation failing would cost the meeting its translation entirely.
        Consent(_speakerUserId, true);
        _settings
            .Setup(d => d.GetVoicePreferenceAsync(_speakerUserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("auth is down"));

        var result = await Service().GenerateRoutesAsync(_roomId);

        Assert.True(result.IsSuccess, "a preference lookup took the whole room down with it");
        Assert.DoesNotContain(_added, r => r.VoiceCloneEnabled);
    }

    [Fact]
    public async Task AGuestHasNoAccountAndIsNeverSeeded()
    {
        // The mesh is bidirectional, so the OTHER participant is a speaker too and is asked
        // about normally — that is not what this pins. What it pins is that the guest, who has
        // no user id to ask about, is never cloned and never invents one.
        var guest = Participant(userId: null, "vi", "en");
        var member = Participant(Guid.NewGuid(), "en", "vi");
        Consent(member.UserId!.Value, true);
        Preference(member.UserId!.Value, true);
        _participants
            .Setup(r => r.GetByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant> { guest, member });

        await Service().GenerateRoutesAsync(_roomId);

        Assert.DoesNotContain(_added.Where(r => r.SourceParticipantId == guest.Id),
            r => r.VoiceCloneEnabled);
        // The member beside them still gets what they asked for — a guest in the room must not
        // switch cloning off for everybody else.
        Assert.Contains(_added.Where(r => r.SourceParticipantId == member.Id),
            r => r.VoiceCloneEnabled);
    }

    // ── a decision made inside the meeting outranks the account default ──────────────────────

    [Fact]
    public async Task TurningItOffInTheMeetingIsNotUndoneByALateJoiner()
    {
        // The worse direction to be wrong in. A speaker who switched cloning off here already has
        // routes saying false; re-seeding from their account preference when somebody joins would
        // silently start cloning them again, mid-meeting, with no interaction of their own.
        Consent(_speakerUserId, true);
        Preference(_speakerUserId, true);
        _existing = new List<TranslationRoomAudioRoute>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TranslationRoomId = _roomId,
                SourceParticipantId = _speaker.Id,
                TargetParticipantId = _listener.Id,
                SourceLanguage = "vi",
                TargetLanguage = "en",
                Status = AudioRouteStatus.PENDING.ToString(),
                VoiceCloneEnabled = false,
            },
        };

        var joiner = Participant(Guid.NewGuid(), "en", "vi");
        _participants
            .Setup(r => r.GetByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant> { _speaker, _listener, joiner });

        await Service().AddRoutesForParticipantAsync(_roomId, joiner.Id);

        Assert.DoesNotContain(_added.Where(r => r.SourceParticipantId == _speaker.Id),
            r => r.VoiceCloneEnabled);
    }

    [Fact]
    public async Task ALateJoinerWithNoRoutesYetStillGetsTheirPreference()
    {
        // The other half: a joiner has nothing in this room to inherit from, so their account
        // preference is the only thing that can speak for them — which is the whole point of
        // WT-401 for anyone who arrives after the room started.
        var joinerUserId = Guid.NewGuid();
        Consent(joinerUserId, true);
        Preference(joinerUserId, true);
        var joiner = Participant(joinerUserId, "en", "vi");

        _participants
            .Setup(r => r.GetByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant> { _speaker, joiner });

        await Service().AddRoutesForParticipantAsync(_roomId, joiner.Id);

        Assert.Contains(_added.Where(r => r.SourceParticipantId == joiner.Id),
            r => r.VoiceCloneEnabled);
    }

    // ── cost ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheMeshDoesNotAskAuthOncePerPair()
    {
        // GenerateRoutesAsync is O(n^2) in participants and the seed is per SPEAKER. Without the
        // per-call cache a six-person room would make sixty round trips to learn six facts, on
        // the path that starts every meeting.
        Consent(_speakerUserId, true);
        Preference(_speakerUserId, true);
        var third = Participant(Guid.NewGuid(), "ja", "en");
        _participants
            .Setup(r => r.GetByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant> { _speaker, _listener, third });

        await Service().GenerateRoutesAsync(_roomId);

        _consent.Verify(
            d => d.HasVoiceCloneConsentAsync(_speakerUserId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
