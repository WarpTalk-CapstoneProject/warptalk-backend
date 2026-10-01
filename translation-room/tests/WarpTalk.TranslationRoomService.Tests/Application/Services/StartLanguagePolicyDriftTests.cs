using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-708 — the workspace language whitelist (L1) holds at START, not only at booking time.
///
/// THE DRIFT THESE PIN
///   A meeting's languages (L2) are validated against L1 when the room is created and when it is
///   edited (WT-466, WT-707) and by nothing afterwards. An admin who narrows L1 on Monday leaves
///   every room booked before Monday carrying languages the workspace no longer allows — and Start
///   published that stored set straight to the AI workers. The owner's setting was enforced
///   against the booking form and against nothing that actually spends money on STT and TTS.
///
///   So the whole rule lives on what reaches Redis. Asserting that Start returned success would
///   pass against the bug: the bug IS a successful start. These cases read the published
///   `meeting:{id}:target_languages` value, because that key is the contract with the workers.
///
/// THE FOUR OUTCOMES, AND WHY EACH ONE IS SEPARATE
///   narrowed   → the meeting runs in what is left, and the host is told what is not.
///   no overlap → refused, naming both sets. Starting it would open a billable room that
///                translates into nothing, and the host would be left diagnosing silence.
///   L1 empty   → unrestricted. Reading empty the other way would refuse every meeting in every
///                workspace that never configured languages, which is most of them.
///   lookup bad → unchanged. Start fails OPEN on workspace checks by long-standing decision; a
///                WorkspaceService outage must not become "no meeting in the product can begin".
/// </summary>
public class StartLanguagePolicyDriftTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepository = new();
    private readonly Mock<ITranslationRoomSessionRepository> _sessionRepository = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _workspaceMeetingPolicy = new();
    private readonly Mock<ITranslationRoomAudioRouteService> _audioRouteService = new();
    private readonly Mock<IAudioRouteEventProcessor> _audioRouteEventProcessor = new();
    private readonly Mock<IRedisStateRepository> _redis = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _sut;

    /// <summary>Everything written through <c>StringSetAsync</c>, keyed exactly as published.</summary>
    private readonly Dictionary<string, string> _published = new(StringComparer.Ordinal);

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    public StartLanguagePolicyDriftTests()
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_roomRepository.Object);
        _unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participantRepository.Object);
        _unitOfWork.Setup(u => u.TranslationRoomSessionRepository).Returns(_sessionRepository.Object);

        // Start takes the room live through a compare-and-set; this request always wins it, so
        // the publish under test is the one the winner makes.
        _roomRepository
            .Setup(r => r.TryTransitionStatusAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _participantRepository
            .Setup(p => p.CountSeatHoldingParticipantsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _participantRepository
            .Setup(p => p.CountEverJoinedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _participantRepository
            .Setup(p => p.GetByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>());

        // The tenant is live and the mesh builds cleanly unless a case says otherwise; neither is
        // what these tests are about, and a null Task from a bare substitute would throw before
        // the assertion under test was ever reached.
        _workspaceMeetingPolicy
            .Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _audioRouteService
            .Setup(s => s.GenerateRoutesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new List<TranslationRoomAudioRouteDto>()));
        _audioRouteEventProcessor
            .Setup(p => p.ProcessEventAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _redis
            .Setup(r => r.StringSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync((string key, string value, TimeSpan? _) =>
            {
                _published[key] = value;
                return true;
            });

        _sut = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _unitOfWork.Object,
            Mock.Of<ILanguagePolicy>(),
            _audioRouteEventProcessor.Object,
            _audioRouteService.Object,
            Mock.Of<IUserSettingsDirectory>(),
            _workspaceMeetingPolicy.Object,
            Mock.Of<IWorkspaceMemberDirectory>(),
            Mock.Of<WarpTalk.Shared.Interfaces.IEmailService>(),
            Mock.Of<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>(),
            redisStateRepository: _redis.Object);
    }

    /// <summary>
    /// Three languages booked, the workspace now allows two of them. The room is started by its
    /// own host so no permission clause is in play.
    /// </summary>
    private TranslationRoom GivenRoom(string status = "SCHEDULED")
    {
        var room = new TranslationRoom
        {
            Id = RoomId,
            WorkspaceId = WorkspaceId,
            HostId = HostId,
            Title = "Weekly sync",
            TranslationRoomCode = "abc-defg-hij",
            Status = status,
            TranslationRoomType = "MEETING",
            SourceLanguage = "vi",
            TargetLanguages = "[\"vi\",\"en\",\"es\"]",
            Settings = "{\"requires_approval\":false,\"artifact_access\":\"HOST_ONLY\"}",
        };

        _roomRepository.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        return room;
    }

    private void GivenWorkspaceAllows(params string[] languages) =>
        _workspaceMeetingPolicy
            .Setup(p => p.GetAllowedLanguagesAsync(WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<string>>(languages));

    /// <summary>What the AI workers were told this meeting translates into.</summary>
    private List<string> PublishedTargetLanguages() =>
        _published.TryGetValue($"meeting:{RoomId}:target_languages", out var json)
            ? JsonSerializer.Deserialize<List<string>>(json)!
            : new List<string>();

    // ── narrowed: run with what is left, and say what is not ──────────────────────

    /// <summary>
    /// THE CASE THE TICKET EXISTS FOR. The booking says vi/en/es; the admin has since cut es.
    /// Start must publish vi and en and nothing else — es reaching the workers is the bug, and it
    /// is invisible from the outside because the start itself succeeds either way.
    /// </summary>
    [Fact]
    public async Task Start_PublishesOnlyTheLanguagesTheWorkspaceStillAllows()
    {
        var room = GivenRoom();
        GivenWorkspaceAllows("vi", "en");

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
        PublishedTargetLanguages().Should().Equal("vi", "en");
    }

    /// <summary>
    /// And the host is told, in the response to the button they just pressed. Without this the
    /// meeting quietly runs in fewer languages than it was booked in and nobody can see why —
    /// which is the same silent narrowing the whole ticket is about, moved one step later.
    /// </summary>
    [Fact]
    public async Task Start_TellsTheHostWhichLanguagesWereDropped()
    {
        GivenRoom();
        GivenWorkspaceAllows("vi", "en");

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        var notice = result.Value!.LanguagePolicyNotice;
        notice.Should().NotBeNull();
        notice!.Requested.Should().Equal("vi", "en", "es");
        notice.Effective.Should().Equal("vi", "en");
        notice.Dropped.Should().Equal("es");
        notice.Message.Should().Contain("es").And.Contain("vi, en");
    }

    /// <summary>
    /// Regional codes on either side are the same language. A workspace that stored "vi-VN" and a
    /// room that stored "vi" agree, and a raw string comparison would refuse a language the owner
    /// explicitly allowed — the exact fold the create/edit path already applies.
    /// </summary>
    [Fact]
    public async Task Start_ComparesNormalizedCodes_SoARegionalWhitelistStillMatches()
    {
        GivenRoom();
        GivenWorkspaceAllows("vi-VN", "en-US");

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        PublishedTargetLanguages().Should().Equal("vi", "en");
    }

    // ── no overlap: refuse, and say why ───────────────────────────────────────────

    /// <summary>
    /// Nothing survives, so there is nothing to translate into. Refused rather than started with
    /// an empty target list: to every worker and every client an empty list is indistinguishable
    /// from configuration that failed to arrive, so the host would be debugging silence instead of
    /// reading a sentence that names the problem.
    /// </summary>
    [Fact]
    public async Task Start_IsRefused_WhenNoLanguageOfTheMeetingIsAllowedAnyMore()
    {
        var room = GivenRoom();
        GivenWorkspaceAllows("ja");

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);

        // Both sets, because either alone sends the reader hunting: the host knows what they
        // booked and not what changed; an admin knows the policy and not the booking.
        result.Error.Should().Contain("vi, en, es").And.Contain("ja");
    }

    /// <summary>
    /// And the refusal is complete: the room is untouched and the workers were told nothing. A
    /// refusal that had already flipped the status or published half a configuration would leave
    /// a room the host cannot start and cannot get back.
    /// </summary>
    [Fact]
    public async Task Start_LeavesTheRoomAlone_WhenItRefuses()
    {
        var room = GivenRoom();
        GivenWorkspaceAllows("ja");

        await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        room.Status.Should().Be("SCHEDULED");
        room.StartedAt.Should().BeNull();
        _published.Should().BeEmpty();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── unrestricted and unreadable: nothing changes ──────────────────────────────

    /// <summary>
    /// EMPTY MEANS UNRESTRICTED, the same reading every other consumer of this list applies. Most
    /// workspaces never configure languages, so reading empty as "allow nothing" would refuse to
    /// start almost every meeting in the product.
    /// </summary>
    [Fact]
    public async Task Start_IsUnchanged_WhenTheWorkspaceSetsNoWhitelist()
    {
        GivenRoom();
        GivenWorkspaceAllows();

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.LanguagePolicyNotice.Should().BeNull();
        PublishedTargetLanguages().Should().Equal("vi", "en", "es");
    }

    /// <summary>
    /// The lookup failing is not the same as the languages being refused. Start's workspace checks
    /// fail OPEN by long-standing decision — join and start carried no WorkspaceService dependency
    /// at all before those checks existed, and turning an outage into "nobody in the product can
    /// enter a meeting" is a far worse outcome than one meeting running the languages it was
    /// booked with. This change must not quietly move that line.
    /// </summary>
    [Fact]
    public async Task Start_IsUnchanged_WhenTheWhitelistCannotBeRead()
    {
        GivenRoom();
        _workspaceMeetingPolicy
            .Setup(p => p.GetAllowedLanguagesAsync(WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<string>>("workspace service unreachable", ErrorCodes.ServiceUnavailable));

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.LanguagePolicyNotice.Should().BeNull();
        PublishedTargetLanguages().Should().Equal("vi", "en", "es");
    }

    /// <summary>
    /// The same, for a lookup that throws rather than answering a failure — a gRPC channel that is
    /// simply gone does not come back as a Result.
    /// </summary>
    [Fact]
    public async Task Start_IsUnchanged_WhenTheWhitelistLookupThrows()
    {
        GivenRoom();
        _workspaceMeetingPolicy
            .Setup(p => p.GetAllowedLanguagesAsync(WorkspaceId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel is gone"));

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        PublishedTargetLanguages().Should().Equal("vi", "en", "es");
    }

    // ── the restart branch ────────────────────────────────────────────────────────

    /// <summary>
    /// Re-Start on an already-running room is the idempotent repair path, and it REPUBLISHES the
    /// room's languages — so it is a drift check like any other start. Without this, a host whose
    /// client retried mid-call would push the forbidden languages straight back at the workers and
    /// undo the narrowing the first start applied.
    /// </summary>
    [Fact]
    public async Task Restart_AlsoPublishesOnlyTheAllowedLanguages()
    {
        GivenRoom("IN_PROGRESS");
        GivenWorkspaceAllows("vi", "en");

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        PublishedTargetLanguages().Should().Equal("vi", "en");
        result.Value!.LanguagePolicyNotice!.Dropped.Should().Equal("es");
    }

    /// <summary>
    /// And it refuses on no overlap, for the reason the publish exists: the re-Start's whole job on
    /// this branch is to hand the workers a configuration, and there is no allowed configuration to
    /// hand them.
    ///
    /// The MEETING is not ended, nobody is kicked and nobody is switched — a refused re-Start
    /// leaves the room IN_PROGRESS with everyone in it speaking what they already chose. That is
    /// the deliberate line: this narrows what the service PRODUCES, never who may sit here.
    /// </summary>
    [Fact]
    public async Task Restart_IsRefused_WhenNoLanguageOfTheMeetingIsAllowedAnyMore()
    {
        var room = GivenRoom("IN_PROGRESS");
        GivenWorkspaceAllows("ja");

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        room.Status.Should().Be("IN_PROGRESS");
        _published.Should().BeEmpty();
    }
}
