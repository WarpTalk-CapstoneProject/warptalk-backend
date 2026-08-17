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
/// WT-433 — a pair that stops needing translation must lose its route.
///
/// The mesh loop skipped such a pair with `continue`, BEFORE the branch that refreshes a stale
/// route. So a route created while the two differed survived their languages converging, forever,
/// carrying the old pair of codes.
///
/// It is not a harmless leftover. The AI pipeline reads SourceLanguage as the STT hint and
/// TargetLanguage as the dub to synthesize, so one abandoned row produced three unrelated-looking
/// symptoms at once: speech transcribed in the wrong language (a wrong-language hint is how
/// Whisper is made to hallucinate "Hello." over Vietnamese), a dub rendered into a language nobody
/// in the pair had chosen, and — because a speaker's dub tracks are created per target language
/// found on their routes — a listener whose own language no longer appeared on any of them
/// hearing complete silence while everyone else heard the dub.
/// </summary>
public class StaleRouteRemovalTests
{
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _routes = new();
    private readonly Mock<IAudioRouteCacheService> _cache = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly TranslationRoomAudioRouteService _service;

    private readonly Guid _roomId = Guid.NewGuid();
    private List<TranslationRoomAudioRoute> _removed = new();
    private List<TranslationRoomAudioRoute> _added = new();

    public StaleRouteRemovalTests()
    {
        _uow.Setup(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _uow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participants.Object);
        _uow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_routes.Object);

        var languagePolicy = new Mock<ILanguagePolicy>();
        languagePolicy
            .Setup(p => p.IsTranslationRequired(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string source, string target) => !string.Equals(source, target, StringComparison.OrdinalIgnoreCase));

        var consent = new Mock<IVoiceConsentDirectory>();
        consent.Setup(d => d.HasVoiceCloneConsentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var settings = new Mock<IUserSettingsDirectory>();
        settings.Setup(d => d.GetVoicePreferenceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserVoicePreference?)null);

        _service = new TranslationRoomAudioRouteService(
            _uow.Object,
            _cache.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            languagePolicy.Object,
            consent.Object,
            settings.Object,
            NullLogger<TranslationRoomAudioRouteService>.Instance);

        _rooms.Setup(r => r.GetByIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom
            {
                Id = _roomId,
                Status = "IN_PROGRESS",
                SourceLanguage = "en",
                // jsonb in production, not a comma-separated list. The generator used to
                // String.Split(',') this, which turns ["en","vi"] into the tokens `["en` and
                // `"vi"]` — latent only because the fallback that consumes it needs a participant
                // with no listen language at all.
                TargetLanguages = """["en","vi"]""",
            });
    }

    private TranslationRoomParticipant Participant(string name, string speak, string listen) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = _roomId,
        UserId = Guid.NewGuid(),
        DisplayName = name,
        Role = "participant",
        SpeakLanguage = speak,
        ListenLanguage = listen,
        Status = "CONNECTED",
        ConnectionType = "web",
    };

    private TranslationRoomAudioRoute Route(Guid source, Guid target, string sourceLang, string targetLang) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = _roomId,
        SourceParticipantId = source,
        TargetParticipantId = target,
        SourceLanguage = sourceLang,
        TargetLanguage = targetLang,
        Status = AudioRouteStatus.PENDING.ToString(),
    };

    private void Arrange(List<TranslationRoomParticipant> roster, List<TranslationRoomAudioRoute> existing)
    {
        _participants.Setup(p => p.GetByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(roster);
        _routes.Setup(r => r.GetRoutesByRoomIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _routes.Setup(r => r.RemoveRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TranslationRoomAudioRoute>, CancellationToken>((rs, _) => _removed = rs.ToList())
            .Returns(Task.CompletedTask);
        _routes.Setup(r => r.AddRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TranslationRoomAudioRoute>, CancellationToken>((rs, _) => _added = rs.ToList())
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task AConvergedPairLosesTheRouteItNoLongerNeeds()
    {
        // Both on Vietnamese now. The en→vi row is from before one of them switched.
        var speaker = Participant("Tuan", speak: "vi", listen: "vi");
        var listener = Participant("Tu", speak: "vi", listen: "vi");
        var stale = Route(speaker.Id, listener.Id, "en", "vi");

        Arrange([speaker, listener], [stale]);

        var result = await _service.GenerateRoutesAsync(_roomId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _removed.Should().ContainSingle().Which.Id.Should().Be(stale.Id);
        _added.Should().BeEmpty();
    }

    [Fact]
    public async Task TheProductionRoomIsRepaired()
    {
        // Reproduced from warptalk_translation_room on 2026-08-16, room 01a008ac. Six routes, all
        // written in one batch, and the SAME speaker carried two different source languages —
        // Tuan was `vi` towards Ky and `en` towards Tu.
        var tu = Participant("Tu", speak: "en", listen: "vi");
        var ky = Participant("Ky", speak: "en", listen: "en");
        var tuan = Participant("Tuan", speak: "vi", listen: "vi");

        var tuanToTu = Route(tuan.Id, tu.Id, "en", "vi");   // stale: vi→vi, needs nothing
        var tuToKy = Route(tu.Id, ky.Id, "en", "vi");       // stale: en→en, needs nothing
        var kyToTu = Route(ky.Id, tu.Id, "en", "vi");       // correct
        var tuanToKy = Route(tuan.Id, ky.Id, "vi", "en");   // correct
        var kyToTuan = Route(ky.Id, tuan.Id, "en", "vi");   // correct
        var tuToTuan = Route(tu.Id, tuan.Id, "en", "vi");   // correct

        Arrange([tu, ky, tuan], [tuanToTu, tuToKy, kyToTu, tuanToKy, kyToTuan, tuToTuan]);

        await _service.GenerateRoutesAsync(_roomId, CancellationToken.None);

        _removed.Select(r => r.Id).Should().BeEquivalentTo([tuanToTu.Id, tuToKy.Id]);
    }

    [Fact]
    public async Task ARouteThatIsStillNeededIsRefreshedRatherThanRemoved()
    {
        // The neighbouring case, and the one that already worked: still a translated pair, but the
        // codes moved. Deleting it here would drop the dub for a pair that still needs one.
        var speaker = Participant("Tuan", speak: "ja", listen: "ja");
        var listener = Participant("Tu", speak: "vi", listen: "vi");
        var existing = Route(speaker.Id, listener.Id, "en", "vi");

        Arrange([speaker, listener], [existing]);

        await _service.GenerateRoutesAsync(_roomId, CancellationToken.None);

        _removed.Should().BeEmpty();
        existing.SourceLanguage.Should().Be("ja");
        existing.TargetLanguage.Should().Be("vi");
    }

    [Fact]
    public async Task AJoinAlsoClearsAPairThatNoLongerNeedsTranslating()
    {
        // AddRoutesForParticipantAsync carried the same `continue`, and its own comment says the
        // participant row is reused on a rejoin — so the route it describes as possibly stale was
        // exactly the one it could not reach.
        var resident = Participant("Ky", speak: "vi", listen: "vi");
        var rejoiner = Participant("Tu", speak: "vi", listen: "vi");
        var stale = Route(rejoiner.Id, resident.Id, "en", "vi");

        Arrange([resident, rejoiner], [stale]);

        await _service.AddRoutesForParticipantAsync(_roomId, rejoiner.Id, CancellationToken.None);

        _removed.Should().ContainSingle().Which.Id.Should().Be(stale.Id);
    }

    [Fact]
    public async Task AJoinNeverTouchesAPairTheJoinerIsNotPartOf()
    {
        // The whole point of the incremental path. A converged pair between two OTHER people is
        // not this method's business, however stale it looks.
        var a = Participant("A", speak: "vi", listen: "vi");
        var b = Participant("B", speak: "vi", listen: "vi");
        var joiner = Participant("C", speak: "vi", listen: "vi");
        var otherPairsRoute = Route(a.Id, b.Id, "en", "vi");

        Arrange([a, b, joiner], [otherPairsRoute]);

        await _service.AddRoutesForParticipantAsync(_roomId, joiner.Id, CancellationToken.None);

        _removed.Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingAStaleRouteTellsTheAiWorkers()
    {
        // The workers' only source of route rows is this broadcast. A deletion that is not
        // published leaves them translating from the row they still hold.
        var speaker = Participant("Tuan", speak: "vi", listen: "vi");
        var listener = Participant("Tu", speak: "vi", listen: "vi");
        Arrange([speaker, listener], [Route(speaker.Id, listener.Id, "en", "vi")]);

        await _service.GenerateRoutesAsync(_roomId, CancellationToken.None);

        _cache.Verify(c => c.PublishRoutesUpdateAsync(_roomId, It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TargetLanguagesIsReadAsJsonNotAsACommaSeparatedList()
    {
        // A participant with no listen language falls back to "the first configured target that
        // is not the source". Split(',') over `["en","vi"]` yields `["en` and `"vi"]`, so that
        // fallback used to hand a bracket-and-quote token to the language policy as if it were a
        // language code.
        var speaker = Participant("Tuan", speak: "en", listen: "en");
        var listener = Participant("Tu", speak: "vi", listen: null!);

        Arrange([speaker, listener], []);

        await _service.GenerateRoutesAsync(_roomId, CancellationToken.None);

        _added.Should().NotBeEmpty();
        _added.Should().OnlyContain(r =>
            r.TargetLanguage == "vi" || r.TargetLanguage == "en");
        _added.Should().NotContain(r => r.TargetLanguage.Contains("[") || r.TargetLanguage.Contains("\""));
    }
}
