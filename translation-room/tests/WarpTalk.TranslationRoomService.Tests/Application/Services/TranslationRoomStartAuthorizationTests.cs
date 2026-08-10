using System;
using System.Collections.Generic;
using System.Linq;
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
/// WT-341 — who may take a room live.
///
/// The rule used to be one line, <c>room.HostId != callerId</c>, and its failure mode was total: a
/// host who was busy, ill, or simply late made their own meeting permanently unstartable. Nobody
/// else could open it — not another invitee, not a participant already waiting in the lobby, not
/// even the workspace owner — so the meeting was lost rather than merely delayed.
///
/// The replacement is not "anyone may start anything". It is the room's OWN
/// <c>requires_approval</c> setting, which already decides whether a joiner lands CONNECTED or
/// WAITING, and therefore already means "entry is the host's decision":
///
///   requires_approval = true   →  host only, exactly as before.
///   requires_approval = false  →  anyone entitled to be in the room.
///
/// These tests pin BOTH directions, because either one alone is a bug. Losing the first hands a
/// guest the power to open a room whose lobby only the host can clear. Losing the second restores
/// the deadlock this ticket exists to remove.
///
/// The entitlement clause has its own test below. It is the one most likely to be "simplified"
/// away later, and dropping it would let any authenticated stranger holding a room id start
/// someone else's meeting — which is not merely a permission bug, it starts billing their
/// workspace for STT and TTS.
/// </summary>
public class TranslationRoomStartAuthorizationTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepository = new();
    private readonly Mock<IWorkspaceMemberDirectory> _workspaceMemberDirectory = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _workspaceMeetingPolicy = new();
    private readonly Mock<ITranslationRoomAudioRouteService> _audioRouteService = new();
    private readonly Mock<IAudioRouteEventProcessor> _audioRouteEventProcessor = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _sut;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    private const string InvitedEmail = "invited@example.com";

    /// <summary>
    /// Written the way the column actually holds it. <see cref="Domain.ValueObjects.TranslationRoomSettings"/>
    /// carries <c>[JsonPropertyName("requires_approval")]</c>, so a fixture spelled
    /// <c>requiresApproval</c> would silently fall back to the property default — which is
    /// <c>true</c>, and would make the permissive cases below pass for the wrong reason.
    /// </summary>
    private const string ApprovalRequiredSettings = "{\"requires_approval\":true,\"artifact_access\":\"HOST_ONLY\"}";
    private const string OpenSettings = "{\"requires_approval\":false,\"artifact_access\":\"HOST_ONLY\"}";

    public TranslationRoomStartAuthorizationTests()
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_roomRepository.Object);
        _unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participantRepository.Object);

        _participantRepository
            .Setup(p => p.CountSeatHoldingParticipantsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _participantRepository
            .Setup(p => p.GetByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>());

        // The tenant is live and the route mesh builds cleanly unless a case says otherwise —
        // neither is what these tests are about, and a null Task from a bare substitute would
        // throw before the assertion under test was ever reached.
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

        _sut = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _unitOfWork.Object,
            Mock.Of<ILanguagePolicy>(),
            _audioRouteEventProcessor.Object,
            _audioRouteService.Object,
            Mock.Of<IUserSettingsDirectory>(),
            _workspaceMeetingPolicy.Object,
            // Answers false to everything: these cases are about the room's own clauses. The
            // Owner/Admin branch gets its own case, which sets this up explicitly.
            _workspaceMemberDirectory.Object,
            Mock.Of<WarpTalk.Shared.Interfaces.IEmailService>(),
            Mock.Of<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>());
    }

    // ── requires_approval = true: host only, unchanged ────────────────────────────

    [Fact]
    public async Task Start_ShouldSucceed_ForTheHost_WhenTheRoomRequiresApproval()
    {
        var room = GivenRoom(ApprovalRequiredSettings);

        var result = await _sut.StartTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
    }

    /// <summary>
    /// The half of the rule that must NOT be relaxed. An approval-gated room parks every non-host
    /// in the lobby and the host is the only person who can admit them, so opening it without the
    /// host produces a running meeting whose door nobody can answer — worse than the deadlock,
    /// because it looks like it worked.
    /// </summary>
    [Fact]
    public async Task Start_ShouldRefuse_ANonHost_WhenTheRoomRequiresApproval()
    {
        var room = GivenRoom(ApprovalRequiredSettings, invitedEmails: new[] { InvitedEmail });

        var result = await _sut.StartTranslationRoomAsync(RoomId, Guid.NewGuid(), InvitedEmail);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        // The refusal has to say WHY, because the fix is a setting the host can change.
        result.Error.Should().Contain("approval");
        room.Status.Should().Be("SCHEDULED");
    }

    // ── requires_approval = false: anyone entitled to be in the room ──────────────

    /// <summary>The deadlock itself: before WT-341 this returned "Only the host can start the room."</summary>
    [Fact]
    public async Task Start_ShouldSucceed_ForAnInvitee_WhenTheRoomDoesNotRequireApproval()
    {
        var room = GivenRoom(OpenSettings, invitedEmails: new[] { InvitedEmail });

        var result = await _sut.StartTranslationRoomAsync(RoomId, Guid.NewGuid(), InvitedEmail);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
        room.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Start_ShouldSucceed_ForAParticipantAlreadyWaitingInTheRoom()
    {
        var waiting = Guid.NewGuid();
        var room = GivenRoom(OpenSettings, participantUserIds: new[] { waiting });

        var result = await _sut.StartTranslationRoomAsync(RoomId, waiting, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
    }

    /// <summary>
    /// A workspace Owner/Admin sees every room in the workspace in their list, so they are exactly
    /// the person asked to rescue a meeting whose host has not shown up.
    /// </summary>
    [Fact]
    public async Task Start_ShouldSucceed_ForAWorkspaceOwnerOrAdmin()
    {
        var admin = Guid.NewGuid();
        var room = GivenRoom(OpenSettings);
        _workspaceMemberDirectory
            .Setup(d => d.IsOwnerOrAdminAsync(WorkspaceId, admin, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.StartTranslationRoomAsync(RoomId, admin, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
    }

    /// <summary>
    /// The boundary. "Not host-only" is not "public": a caller with no host, participant or
    /// invitation claim on this room is still refused, and the room stays SCHEDULED. Deleting the
    /// entitlement check would leave every other test in this file passing.
    /// </summary>
    [Fact]
    public async Task Start_ShouldRefuse_AStranger_EvenWhenTheRoomDoesNotRequireApproval()
    {
        var room = GivenRoom(OpenSettings);

        var result = await _sut.StartTranslationRoomAsync(RoomId, Guid.NewGuid(), "stranger@example.com");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        room.Status.Should().Be("SCHEDULED");
    }

    /// <summary>
    /// A settings blob this cannot parse must NOT read as "approval not required". The parser
    /// falls back to the property default, which is <c>true</c>, so unreadable data keeps the room
    /// host-only — a permission check that fails open on corrupt input is worse than one that is
    /// merely strict.
    /// </summary>
    [Fact]
    public async Task Start_ShouldRefuse_ANonHost_WhenTheSettingsBlobIsUnreadable()
    {
        var room = GivenRoom("{ this is not json", invitedEmails: new[] { InvitedEmail });

        var result = await _sut.StartTranslationRoomAsync(RoomId, Guid.NewGuid(), InvitedEmail);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        room.Status.Should().Be("SCHEDULED");
    }

    private TranslationRoom GivenRoom(
        string settings,
        Guid[]? participantUserIds = null,
        string[]? invitedEmails = null)
    {
        var room = new TranslationRoom
        {
            Id = RoomId,
            HostId = HostId,
            WorkspaceId = WorkspaceId,
            Title = "Quarterly planning",
            TranslationRoomCode = "ABC-DEF-GHI",
            Status = "SCHEDULED",
            TranslationRoomType = "INSTANT",
            MaxParticipants = 10,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = settings,
            IsActive = true,
            DeletedAt = null,
            CreatedAt = DateTime.UtcNow,
            TranslationRoomParticipants = (participantUserIds ?? Array.Empty<Guid>())
                .Select(id => new TranslationRoomParticipant
                {
                    Id = Guid.NewGuid(),
                    TranslationRoomId = RoomId,
                    UserId = id
                })
                .ToList(),
            TranslationRoomInvitations = (invitedEmails ?? Array.Empty<string>())
                .Select(email => new TranslationRoomInvitation
                {
                    Id = Guid.NewGuid(),
                    TranslationRoomId = RoomId,
                    Email = email,
                    Status = "PENDING"
                })
                .ToList()
        };

        _roomRepository
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _roomRepository.Setup(r => r.Query()).Returns(new[] { room }.AsQueryable());

        return room;
    }
}
