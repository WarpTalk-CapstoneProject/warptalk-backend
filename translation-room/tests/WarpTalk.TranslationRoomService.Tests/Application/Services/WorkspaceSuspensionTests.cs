using FluentAssertions;
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
/// Suspending a workspace has to stop the things that cost money.
///
/// It never did. The gRPC gate behind meeting creation loaded the workspace and checked
/// membership, languages and entitlements without ever reading is_active, and the join and start
/// paths asked WorkspaceService nothing at all — so a suspended tenant kept creating rooms,
/// admitting participants and streaming audio through STT and TTS. It looked like it worked
/// because suspension DOES correctly block document upload and new workspace invitations.
///
/// The rule these pin: a suspended workspace may not BEGIN billable work — no new room, nobody new
/// admitted, no room taken live — but work already in flight is never interrupted. Killing a call
/// mid-sentence is a worse failure than letting the current one finish, and the spend it
/// represents is bounded by that one call.
///
/// Every case here pairs a denial with the identical request in a LIVE workspace. A gate that
/// denied everybody would satisfy the denials alone, and the allow cases are the hot path for
/// every meeting in the product.
/// </summary>
public class WorkspaceSuspensionTests
{
    private const string RoomCode = "abc-defg-hij";
    private const string SuspendedMessage =
        "This workspace is suspended. Contact your administrator to restore it.";

    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepo = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _mockAudioRouteRepo = new();
    private readonly Mock<ITranslationRoomSessionRepository> _mockSessionRepo = new();
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy = new();
    private readonly Mock<IAudioRouteEventProcessor> _mockAudioRouteEventProcessor = new();
    private readonly Mock<ITranslationRoomAudioRouteService> _mockAudioRouteService = new();
    private readonly Mock<IUserSettingsDirectory> _mockUserSettingsDirectory = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy = new();
    private readonly Mock<WarpTalk.Shared.Interfaces.IEmailService> _mockEmailService = new();
    private readonly Mock<Microsoft.Extensions.Logging.ILogger<
        WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>> _mockLogger = new();

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    public WorkspaceSuspensionTests()
    {
        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_mockAudioRouteRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomSessionRepository).Returns(_mockSessionRepo.Object);

        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>());
        _mockAudioRouteService.Setup(s => s.GenerateRoutesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new List<TranslationRoomAudioRouteDto>()));

        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockLanguagePolicy.Setup(v => v.ValidateParticipantLanguagesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TranslationRoom>()))
            .ReturnsAsync((string?)null);

        // Default: a live tenant that permits creation. Each test overrides only what it is about.
        ArrangeWorkspaceIsLive();
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUow.Object,
            _mockLanguagePolicy.Object,
            _mockAudioRouteEventProcessor.Object,
            _mockAudioRouteService.Object,
            _mockUserSettingsDirectory.Object,
            _mockWorkspaceMeetingPolicy.Object,
            // Added by the workspace-wide room-list change; these tests are about tenant
            // suspension and never read the directory, so a bare substitute is enough.
            new Mock<IWorkspaceMemberDirectory>().Object,
            _mockEmailService.Object,
            _mockLogger.Object);
    }

    private void ArrangeWorkspaceIsLive() =>
        _mockWorkspaceMeetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

    /// <summary>
    /// What WorkspaceMeetingPolicyGrpcClient returns once WorkspaceService reports is_active false —
    /// a Forbidden carrying the workspace's own wording.
    /// </summary>
    private void ArrangeWorkspaceIsSuspended() =>
        _mockWorkspaceMeetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(SuspendedMessage, ErrorCodes.Forbidden));

    private TranslationRoom ArrangeRoom(Guid hostId, string status)
    {
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            HostId = hostId,
            TranslationRoomCode = RoomCode,
            Status = status,
            TranslationRoomType = "INSTANT",
            Settings = "{\"requires_approval\":false,\"history_access\":\"HostOnly\"}"
        };

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(RoomCode, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockRoomRepo.Setup(r => r.GetByIdAsync(room.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        return room;
    }

    private Task<Result<JoinTranslationRoomResponse>> JoinAs(Guid userId) =>
        _service.JoinTranslationRoomAsync(new JoinTranslationRoomRequest(RoomCode, "User", "en", "vi"), userId);

    // ── Creating a meeting ───────────────────────────────────────────────────────

    /// <summary>
    /// The creation gate lives in WorkspaceService (see WorkspaceDirectoryServiceTests for the
    /// is_active read itself). This asserts the room service honours a denial rather than logging
    /// it and carrying on, and that no room row is written on the way out.
    /// </summary>
    [Fact]
    public async Task Create_IsDenied_WhenTheWorkspaceIsSuspended()
    {
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(SuspendedMessage, ErrorCodes.Forbidden));

        var result = await _service.CreateTranslationRoomAsync(
            new CreateTranslationRoomRequest(
                WorkspaceId: _workspaceId,
                Title: "Standup",
                Description: null,
                TranslationRoomType: "INSTANT",
                MaxParticipants: null,
                SourceLanguage: "en",
                TargetLanguages: new List<string> { "vi" },
                Settings: null,
                ScheduledAt: null,
                InvitedEmails: null),
            Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        result.Error.Should().Contain("suspended");
        _mockRoomRepo.Verify(r => r.AddAsync(It.IsAny<TranslationRoom>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_IsAllowed_WhenTheWorkspaceIsLive()
    {
        _mockRoomRepo.Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.CreateTranslationRoomAsync(
            new CreateTranslationRoomRequest(
                WorkspaceId: _workspaceId,
                Title: "Standup",
                Description: null,
                TranslationRoomType: "INSTANT",
                MaxParticipants: null,
                SourceLanguage: "en",
                TargetLanguages: new List<string> { "vi" },
                Settings: null,
                ScheduledAt: null,
                InvitedEmails: null),
            Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        _mockRoomRepo.Verify(r => r.AddAsync(It.IsAny<TranslationRoom>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Joining a room ───────────────────────────────────────────────────────────

    /// <summary>
    /// A room created while the tenant was live outlives the suspension, and every participant who
    /// enters it opens a fresh billable STT/TTS stream — so the check cannot live at creation
    /// alone.
    /// </summary>
    [Fact]
    public async Task Join_IsDenied_WhenTheWorkspaceIsSuspended()
    {
        ArrangeRoom(Guid.NewGuid(), status: "WAITING");
        ArrangeWorkspaceIsSuspended();

        var result = await JoinAs(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        result.Error.Should().Contain("suspended");
        _mockParticipantRepo.Verify(
            p => p.AddAsync(It.IsAny<TranslationRoomParticipant>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Join_IsAllowed_WhenTheWorkspaceIsLive()
    {
        ArrangeRoom(Guid.NewGuid(), status: "WAITING");

        var result = await JoinAs(Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        _mockParticipantRepo.Verify(
            p => p.AddAsync(It.IsAny<TranslationRoomParticipant>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Not even the host. Suspension is a decision about the TENANT, so the one identity that is
    /// exempt from the room's own capacity rule is not exempt from this one.
    /// </summary>
    [Fact]
    public async Task Join_IsDenied_ForTheHostToo_WhenTheWorkspaceIsSuspended()
    {
        var hostId = Guid.NewGuid();
        ArrangeRoom(hostId, status: "WAITING");
        ArrangeWorkspaceIsSuspended();

        var result = await JoinAs(hostId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    // ── Taking a room live ───────────────────────────────────────────────────────

    /// <summary>
    /// Start is the transition that actually turns on billable AI: it opens a translation session
    /// and hands the room to the audio routing state machine. A room scheduled before the
    /// suspension has to be stopped here, because its creation already happened.
    /// </summary>
    [Fact]
    public async Task Start_IsDenied_WhenTheWorkspaceIsSuspended()
    {
        var hostId = Guid.NewGuid();
        var room = ArrangeRoom(hostId, status: "SCHEDULED");
        ArrangeWorkspaceIsSuspended();

        var result = await _service.StartTranslationRoomAsync(room.Id, hostId, null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        room.Status.Should().Be("SCHEDULED");
        _mockSessionRepo.Verify(
            s => s.AddAsync(It.IsAny<TranslationRoomSession>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_IsAllowed_WhenTheWorkspaceIsLive()
    {
        var hostId = Guid.NewGuid();
        var room = ArrangeRoom(hostId, status: "WAITING");

        var result = await _service.StartTranslationRoomAsync(room.Id, hostId, null);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("IN_PROGRESS");
    }

    // ── The in-progress carve-out ────────────────────────────────────────────────

    /// <summary>
    /// THE DELIBERATE LIMIT of this change, asserted so nobody tightens it by accident.
    ///
    /// Suspension stops meetings from starting; it does not end one that has. A room already
    /// IN_PROGRESS keeps its idempotent re-Start — a host whose client retried mid-call must not be
    /// stranded — and no code path here terminates a live room on suspension. The bill for the call
    /// currently in flight is accepted; it is bounded by that call, and a tenant that cannot create,
    /// enter or start anything else cannot extend it.
    /// </summary>
    [Fact]
    public async Task AnInProgressRoom_IsNotTornDown_WhenTheWorkspaceIsSuspended()
    {
        var hostId = Guid.NewGuid();
        var room = ArrangeRoom(hostId, status: "IN_PROGRESS");
        ArrangeWorkspaceIsSuspended();

        var result = await _service.StartTranslationRoomAsync(room.Id, hostId, null);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("IN_PROGRESS");
    }

    /// <summary>
    /// Resume is PAUSED → IN_PROGRESS, and only a room that already STARTED can be paused. So it is
    /// continuation of a meeting in flight, not the beginning of one, and it stays allowed under
    /// the same rule that leaves an IN_PROGRESS room alone. Pinned because "resume opens a new
    /// numbered translation session" makes it look like a start if you only read that line.
    /// </summary>
    [Fact]
    public async Task Resuming_APausedRoom_StaysAllowed_WhenTheWorkspaceIsSuspended()
    {
        var hostId = Guid.NewGuid();
        var room = ArrangeRoom(hostId, status: "PAUSED");
        ArrangeWorkspaceIsSuspended();

        var result = await _service.ResumeTranslationRoomAsync(room.Id, hostId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("IN_PROGRESS");
    }

    /// <summary>
    /// A suspended tenant must still be able to WIND DOWN. Ending a room costs nothing and is the
    /// only way the host closes out a call that was running when the suspension landed — blocking
    /// it would leave rooms stuck IN_PROGRESS forever.
    /// </summary>
    [Fact]
    public async Task Ending_ARoom_StaysAllowed_WhenTheWorkspaceIsSuspended()
    {
        var hostId = Guid.NewGuid();
        var room = ArrangeRoom(hostId, status: "IN_PROGRESS");
        ArrangeWorkspaceIsSuspended();

        var result = await _service.EndTranslationRoomAsync(room.Id, hostId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("ENDED");
        _mockWorkspaceMeetingPolicy.Verify(
            p => p.EnsureWorkspaceCanHostMeetingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
