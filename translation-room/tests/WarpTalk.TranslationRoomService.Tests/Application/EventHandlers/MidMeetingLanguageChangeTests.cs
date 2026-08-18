using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.EventHandlers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.EventHandlers;

/// <summary>
/// WT-419 — changing the language you speak or hear WHILE the meeting runs.
///
/// TranslationRoomHub.SetSpeakLanguage wrote the new language to a Redis hash and stopped. The
/// audio mesh reads participant.SpeakLanguage — a Postgres column — and only regenerates at Start
/// or on join. So a pair's routes were pinned to the languages they held when they joined.
///
/// The production report, 15 Aug: Tuấn on en/en spoke English, Nhi on vi/vi received neither the
/// dub nor a translated transcript. Both had joined on the same language, so the pair had NO route
/// at all — and nothing in the system would ever create one. STT made it look stranger by being
/// right: it reads the Redis hash, so the speech was transcribed as English and then had nowhere
/// to go.
/// </summary>
public class MidMeetingLanguageChangeTests
{
    private static readonly Guid RoomId = Guid.NewGuid();

    // ── The processor ────────────────────────────────────────────

    [Fact]
    public async Task ChangingSpeakLanguage_PersistsIt_AndRegeneratesTheMesh()
    {
        var userId = Guid.NewGuid();
        var participant = Participant(userId, speak: "vi", listen: "vi");
        var (processor, routeService) = BuildProcessor(participant);

        var result = await processor.ProcessLanguageChangeAsync(RoomId, userId, "en", null);

        result.IsSuccess.Should().BeTrue();
        participant.SpeakLanguage.Should().Be("en", "the column the mesh reads is the one that has to move");
        participant.ListenLanguage.Should().Be("vi", "a hub call that carried no listen language must not blank it");
        routeService.Verify(s => s.GenerateRoutesAsync(RoomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangingListenLanguage_PersistsIt_AndRegeneratesTheMesh()
    {
        var userId = Guid.NewGuid();
        var participant = Participant(userId, speak: "vi", listen: "vi");
        var (processor, routeService) = BuildProcessor(participant);

        await processor.ProcessLanguageChangeAsync(RoomId, userId, null, "en");

        participant.ListenLanguage.Should().Be("en");
        participant.SpeakLanguage.Should().Be("vi");
        routeService.Verify(s => s.GenerateRoutesAsync(RoomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The negative control that keeps this from becoming a broadcast storm. The client re-sends
    /// its language on reconnect and on every render that reconciles it, so "no actual change" is
    /// the COMMON case — and regenerating anyway republishes the whole mesh to every AI worker.
    /// </summary>
    [Fact]
    public async Task ResendingTheSameLanguage_DoesNotRegenerateAnything()
    {
        var userId = Guid.NewGuid();
        var participant = Participant(userId, speak: "vi", listen: "en");
        var (processor, routeService) = BuildProcessor(participant);

        var result = await processor.ProcessLanguageChangeAsync(RoomId, userId, "vi", "en");

        result.IsSuccess.Should().BeTrue();
        routeService.Verify(s => s.GenerateRoutesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// "auto" is not a language. It reaches STT as a free-run hint and is never something a route
    /// can target, so it must not overwrite a real choice on the way through.
    /// </summary>
    [Fact]
    public async Task TheAutoSentinel_IsNotTreatedAsAChoice()
    {
        var userId = Guid.NewGuid();
        var participant = Participant(userId, speak: "vi", listen: "en");
        var (processor, routeService) = BuildProcessor(participant);

        await processor.ProcessLanguageChangeAsync(RoomId, userId, "auto", null);

        participant.SpeakLanguage.Should().Be("vi", "\"auto\" overwrote a real language");
        routeService.Verify(s => s.GenerateRoutesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Somebody can leave between the hub call and this consumer reading it. That is not a failure
    /// worth retrying three times and sending to the DLQ.
    /// </summary>
    [Fact]
    public async Task AUserWhoHasLeft_IsNotAnError()
    {
        var (processor, routeService) = BuildProcessor();

        var result = await processor.ProcessLanguageChangeAsync(RoomId, Guid.NewGuid(), "en", null);

        result.IsSuccess.Should().BeTrue();
        routeService.Verify(s => s.GenerateRoutesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Rejoining leaves the old row behind. Moving the departed one would persist a language change
    /// onto a participant who is not in the meeting, and leave the live one untouched.
    /// </summary>
    [Fact]
    public async Task ARejoinedParticipant_HasTheirLiveRowMoved_NotTheDepartedOne()
    {
        var userId = Guid.NewGuid();
        var departed = Participant(userId, speak: "vi", listen: "vi");
        departed.LeftAt = DateTime.UtcNow.AddMinutes(-10);
        departed.JoinedAt = DateTime.UtcNow.AddMinutes(-30);
        var live = Participant(userId, speak: "vi", listen: "vi");
        live.JoinedAt = DateTime.UtcNow.AddMinutes(-5);

        var (processor, _) = BuildProcessor(departed, live);

        await processor.ProcessLanguageChangeAsync(RoomId, userId, "en", null);

        live.SpeakLanguage.Should().Be("en");
        departed.SpeakLanguage.Should().Be("vi", "a row belonging to a past session was rewritten");
    }

    // ── The mesh itself, with the real policy ────────────────────

    /// <summary>
    /// The production case end to end, against the REAL LanguagePolicy.
    ///
    /// Two people join on the same language, so no route exists — correctly, since a matched pair
    /// needs no translation. One of them then switches to English. A route must be CREATED, not
    /// merely updated: there was nothing there to update, which is precisely why the old code
    /// (whose only route-refresh path was an `isRouteStale` branch over existing rows) could never
    /// have recovered from it even if something had called it.
    /// </summary>
    [Fact]
    public async Task WhenAMatchedPairDiverges_ARouteIsCreatedWhereThereWasNone()
    {
        var tuan = Participant(Guid.NewGuid(), speak: "en", listen: "en");
        var nhi = Participant(Guid.NewGuid(), speak: "vi", listen: "vi");

        var added = new List<TranslationRoomAudioRoute>();
        var service = BuildRouteService(
            participants: new[] { tuan, nhi },
            existingRoutes: new List<TranslationRoomAudioRoute>(),
            onAdd: added.AddRange);

        var result = await service.GenerateRoutesAsync(RoomId);

        result.IsSuccess.Should().BeTrue();
        added.Should().HaveCount(2, "both directions need translating once the pair no longer match");

        var tuanToNhi = added.Single(r => r.SourceParticipantId == tuan.Id);
        tuanToNhi.SourceLanguage.Should().Be("en");
        tuanToNhi.TargetLanguage.Should().Be("vi",
            "this is the exact route whose absence meant Nhi heard nothing while Tuấn spoke English");

        var nhiToTuan = added.Single(r => r.SourceParticipantId == nhi.Id);
        nhiToTuan.SourceLanguage.Should().Be("vi");
        nhiToTuan.TargetLanguage.Should().Be("en");
    }

    /// <summary>
    /// The other direction, and the negative control for the one above: converging on a shared
    /// language must leave no route behind. A stale route would keep dubbing a translation nobody
    /// needs, and keep billing for it.
    /// </summary>
    [Fact]
    public async Task WhenAPairConvergesOnOneLanguage_NoRouteIsCreated()
    {
        var first = Participant(Guid.NewGuid(), speak: "vi", listen: "vi");
        var second = Participant(Guid.NewGuid(), speak: "vi", listen: "vi");

        var added = new List<TranslationRoomAudioRoute>();
        var service = BuildRouteService(
            participants: new[] { first, second },
            existingRoutes: new List<TranslationRoomAudioRoute>(),
            onAdd: added.AddRange);

        await service.GenerateRoutesAsync(RoomId);

        added.Should().BeEmpty("a matched pair needs no translation — that part was never the bug");
    }

    // ── Builders ─────────────────────────────────────────────────

    private static TranslationRoomParticipant Participant(Guid userId, string speak, string listen) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = RoomId,
        UserId = userId,
        DisplayName = "Someone",
        Role = "participant",
        SpeakLanguage = speak,
        ListenLanguage = listen,
        Status = "CONNECTED",
        ConnectionType = "web",
        JoinedAt = DateTime.UtcNow,
    };

    private static (ParticipantLanguageProcessor, Mock<ITranslationRoomAudioRouteService>) BuildProcessor(
        params TranslationRoomParticipant[] participants)
    {
        var participantRepository = new Mock<ITranslationRoomParticipantRepository>();
        participantRepository
            .Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<TranslationRoomParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants.ToList());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(participantRepository.Object);

        var routeService = new Mock<ITranslationRoomAudioRouteService>();
        routeService
            .Setup(s => s.GenerateRoutesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new List<TranslationRoomAudioRouteDto>()));

        return (
            new ParticipantLanguageProcessor(
                unitOfWork.Object, routeService.Object, NullLogger<ParticipantLanguageProcessor>.Instance),
            routeService);
    }

    private static TranslationRoomAudioRouteService BuildRouteService(
        IReadOnlyList<TranslationRoomParticipant> participants,
        List<TranslationRoomAudioRoute> existingRoutes,
        Action<IEnumerable<TranslationRoomAudioRoute>> onAdd)
    {
        var participantRepository = new Mock<ITranslationRoomParticipantRepository>();
        participantRepository
            .Setup(r => r.GetByRoomIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants.ToList());

        var routeRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        routeRepository
            .Setup(r => r.GetRoutesByRoomIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => existingRoutes.ToList());
        routeRepository
            .Setup(r => r.AddRoutesAsync(It.IsAny<IEnumerable<TranslationRoomAudioRoute>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TranslationRoomAudioRoute>, CancellationToken>((routes, _) => onAdd(routes))
            .Returns(Task.CompletedTask);

        var roomRepository = new Mock<ITranslationRoomRepository>();
        roomRepository
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom
            {
                Id = RoomId,
                SourceLanguage = "en",
                TargetLanguages = """["en","vi"]""",
            });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(participantRepository.Object);
        unitOfWork.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(routeRepository.Object);
        unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(roomRepository.Object);

        var consentDirectory = new Mock<IVoiceConsentDirectory>();
        var settingsDirectory = new Mock<IUserSettingsDirectory>();

        return new TranslationRoomAudioRouteService(
            unitOfWork.Object,
            new Mock<IAudioRouteCacheService>().Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            // The REAL policy. A mocked ILanguagePolicy returns false from IsTranslationRequired by
            // default, which would make "no route was created" pass for the wrong reason — the
            // exact failure these tests exist to catch.
            new LanguagePolicy(unitOfWork.Object),
            consentDirectory.Object,
            settingsDirectory.Object,
            Mock.Of<IRedisStateRepository>(),
            NullLogger<TranslationRoomAudioRouteService>.Instance);
    }
}
