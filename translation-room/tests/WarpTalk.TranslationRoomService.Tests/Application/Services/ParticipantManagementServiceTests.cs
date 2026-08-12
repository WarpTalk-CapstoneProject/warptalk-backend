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
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public class ParticipantManagementServiceTests
{
    private readonly Mock<ITranslationRoomRepository> _roomRepositoryMock;
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepositoryMock;
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<ITranslationRoomInvitationRepository> _invitationRepositoryMock;
    private readonly Mock<IWorkspaceMemberDirectory> _workspaceMemberDirectoryMock;
    private readonly Mock<ILogger<TranslationRoomParticipantService>> _loggerMock;
    private readonly TranslationRoomParticipantService _sut;

    public ParticipantManagementServiceTests()
    {
        _roomRepositoryMock = new Mock<ITranslationRoomRepository>();
        _participantRepositoryMock = new Mock<ITranslationRoomParticipantRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _invitationRepositoryMock = new Mock<ITranslationRoomInvitationRepository>();
        _workspaceMemberDirectoryMock = new Mock<IWorkspaceMemberDirectory>();
        _loggerMock = new Mock<ILogger<TranslationRoomParticipantService>>();

        _unitOfWorkMock.Setup(uow => uow.TranslationRoomRepository).Returns(_roomRepositoryMock.Object);
        _unitOfWorkMock.Setup(uow => uow.TranslationRoomParticipantRepository).Returns(_participantRepositoryMock.Object);
        _unitOfWorkMock.Setup(uow => uow.TranslationRoomInvitationRepository).Returns(_invitationRepositoryMock.Object);

        // WT-304 default: nobody is invited, so pre-existing cases keep the exact verdict they had
        // before the invitation clause was added.
        _invitationRepositoryMock
            .Setup(repo => repo.AnyAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Default: caller has no workspace-level privilege, so host identity alone decides.
        _workspaceMemberDirectoryMock
            .Setup(d => d.IsOwnerOrAdminAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _sut = new TranslationRoomParticipantService(
            _unitOfWorkMock.Object,
            _workspaceMemberDirectoryMock.Object,
            _loggerMock.Object
        );
    }

    // The requester here is a stranger: not the host, holds no participant row, and — via the
    // constructor's default IsOwnerOrAdminAsync => false — has no workspace privilege either. WT-313
    // widened this endpoint to workspace Owner/Admin, so this case is what stops that widening from
    // becoming "any authenticated user", the failure mode WT-65 already shipped once.
    [Fact]
    public async Task GetParticipantsAsync_ShouldReturnForbidden_WhenRequesterIsNotInRoom()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = hostId, WorkspaceId = Guid.NewGuid() };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, requesterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), requesterId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    // WT-313 — viewing the participant list is "room host OR any participant OR workspace
    // Owner/Admin". WT-188 widened admission but left this read behind, so a workspace Owner was
    // 403'd by the waiting page's participant poll and never reached the Approve button.

    [Fact]
    public async Task GetParticipantsAsync_ShouldReturnParticipants_WhenRequesterIsWorkspaceOwnerOrAdmin()
    {
        var roomId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var workspaceOwnerId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), WorkspaceId = workspaceId };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        // Not the host, and holds no participant row of their own — the WT-313 reporter's exact state.
        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, workspaceOwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        _participantRepositoryMock.Setup(repo => repo.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>
            {
                new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, DisplayName = "Waiting Guest", Status = "WAITING", Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(), JoinedAt = DateTime.UtcNow }
            });
        _workspaceMemberDirectoryMock
            .Setup(d => d.IsOwnerOrAdminAsync(workspaceId, workspaceOwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), workspaceOwnerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
    }

    // Admin and Owner are one decision to this service: IWorkspaceMemberDirectory collapses both into
    // IsOwnerOrAdminAsync. This asserts the Admin half of that contract is actually reachable here,
    // so a future narrowing of the directory to Owner-only cannot pass unnoticed.
    [Fact]
    public async Task GetParticipantsAsync_ShouldReturnParticipants_WhenRequesterIsWorkspaceAdmin()
    {
        var roomId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var workspaceAdminId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), WorkspaceId = workspaceId };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, workspaceAdminId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        _participantRepositoryMock.Setup(repo => repo.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>
            {
                new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, DisplayName = "Waiting Guest", Status = "WAITING", Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(), JoinedAt = DateTime.UtcNow }
            });
        _workspaceMemberDirectoryMock
            .Setup(d => d.IsOwnerOrAdminAsync(workspaceId, workspaceAdminId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), workspaceAdminId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
    }

    // The case that keeps the widening honest. A plain workspace Member belongs to the workspace but
    // has no business reading the roster of a room they were never invited to and never joined —
    // "is a member of the workspace" must not be mistaken for "may view this room".
    [Fact]
    public async Task GetParticipantsAsync_ShouldReturnForbidden_WhenRequesterIsPlainWorkspaceMember()
    {
        var roomId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var plainMemberId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), WorkspaceId = workspaceId };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, plainMemberId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        // A workspace Member is not an Owner/Admin: the directory says false for them.
        _workspaceMemberDirectoryMock
            .Setup(d => d.IsOwnerOrAdminAsync(workspaceId, plainMemberId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), plainMemberId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        // The roster must not even be read for a caller who cannot see it.
        _participantRepositoryMock.Verify(
            repo => repo.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // WT-65's clause, re-pinned: participation is enough regardless of Status, and it must not depend
    // on WorkspaceService — the waiting page polls this every 3 seconds for everyone in the lobby.
    [Fact]
    public async Task GetParticipantsAsync_ShouldReturnParticipants_WhenRequesterIsWaitingParticipant()
    {
        var roomId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() };
        var requesterRow = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            UserId = requesterId,
            DisplayName = "Waiting Guest",
            Status = "WAITING",
            Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(),
            JoinedAt = DateTime.UtcNow
        };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, requesterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(requesterRow);
        _participantRepositoryMock.Setup(repo => repo.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant> { requesterRow });

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), requesterId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        _workspaceMemberDirectoryMock.Verify(
            d => d.IsOwnerOrAdminAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---------------------------------------------------------------------------------------------
    // WT-304 — invited by email, not yet joined.
    //
    // These drive the real RoomReadAccess predicate rather than a canned bool: the invitation
    // repository mock compiles whatever expression the service hands it and runs it over an
    // in-memory invitation list, so a change to the allow-list or the email matching shows up here.
    // ---------------------------------------------------------------------------------------------

    private void GivenInvitations(params TranslationRoomInvitation[] invitations)
    {
        _invitationRepositoryMock
            .Setup(repo => repo.AnyAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>> predicate, CancellationToken _) =>
                invitations.Any(predicate.Compile()));
    }

    // The WT-304 bug itself: a workspace Member invited by email holds no participant row and is not
    // an Owner/Admin, so before this fix the lobby's 3s poll 403'd forever.
    [Theory]
    [InlineData("PENDING")]
    [InlineData("ACCEPTED")]
    public async Task GetParticipantsAsync_ShouldReturnParticipants_WhenRequesterWasInvitedByEmail(string invitationStatus)
    {
        var roomId = Guid.NewGuid();
        var inviteeId = Guid.NewGuid();
        const string inviteeEmail = "invitee@warptalk.vn";

        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, inviteeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        _participantRepositoryMock.Setup(repo => repo.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>
            {
                new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, DisplayName = "Host", Status = "CONNECTED", Role = TranslationRoomParticipantRole.HOST.ToString(), JoinedAt = DateTime.UtcNow }
            });
        GivenInvitations(new TranslationRoomInvitation { Id = Guid.NewGuid(), TranslationRoomId = roomId, Email = inviteeEmail, Status = invitationStatus });

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), inviteeId, inviteeEmail);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
    }

    // A withdrawn invitation is not an invitation. Nothing writes DECLINED today, which is exactly
    // why this needs pinning: the allow-list is what makes the not-yet-existing revoked states safe.
    [Theory]
    [InlineData("DECLINED")]
    [InlineData("REVOKED")]
    [InlineData("EXPIRED")]
    public async Task GetParticipantsAsync_ShouldReturnForbidden_WhenInvitationNoLongerStands(string invitationStatus)
    {
        var roomId = Guid.NewGuid();
        var inviteeId = Guid.NewGuid();
        const string inviteeEmail = "invitee@warptalk.vn";

        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, inviteeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        GivenInvitations(new TranslationRoomInvitation { Id = Guid.NewGuid(), TranslationRoomId = roomId, Email = inviteeEmail, Status = invitationStatus });

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), inviteeId, inviteeEmail);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    // The pinned plain-Member 403 from the other direction: now that the caller's email reaches the
    // service, "has an email claim" must not become a way in. Only an invitation is.
    [Fact]
    public async Task GetParticipantsAsync_ShouldReturnForbidden_WhenPlainMemberHasEmailButNoInvitation()
    {
        var roomId = Guid.NewGuid();
        var plainMemberId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, plainMemberId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        // Somebody else was invited; this caller was not.
        GivenInvitations(new TranslationRoomInvitation { Id = Guid.NewGuid(), TranslationRoomId = roomId, Email = "someone.else@warptalk.vn", Status = "PENDING" });

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), plainMemberId, "plain.member@warptalk.vn");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        _participantRepositoryMock.Verify(
            repo => repo.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // The ordering requirement, asserted rather than commented: the common poll case — an existing
    // participant — must reach its answer without the invitation round-trip or the gRPC hop.
    [Fact]
    public async Task GetParticipantsAsync_ShouldNotQueryInvitations_WhenRequesterIsAlreadyAParticipant()
    {
        var roomId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() };
        var requesterRow = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            UserId = requesterId,
            DisplayName = "Waiting Guest",
            Status = "WAITING",
            Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(),
            JoinedAt = DateTime.UtcNow
        };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, requesterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(requesterRow);
        _participantRepositoryMock.Setup(repo => repo.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant> { requesterRow });
        GivenInvitations();

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), requesterId, "waiting.guest@warptalk.vn");

        result.IsSuccess.Should().BeTrue();
        _invitationRepositoryMock.Verify(
            repo => repo.AnyAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _workspaceMemberDirectoryMock.Verify(
            d => d.IsOwnerOrAdminAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // A caller with no email claim must not reach the database at all for the invitation clause.
    [Fact]
    public async Task GetParticipantsAsync_ShouldNotQueryInvitations_WhenCallerHasNoEmailClaim()
    {
        var roomId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, requesterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        GivenInvitations();

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), requesterId, null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        _invitationRepositoryMock.Verify(
            repo => repo.AnyAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateParticipantAudioAsync_ShouldReturnForbidden_WhenRequesterIsNotHost()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = hostId };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        var result = await _sut.UpdateParticipantAudioAsync(roomId, participantId, new UpdateParticipantAudioRequest(true), requesterId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task KickParticipantAsync_ShouldSetStatusToKicked_WhenRequesterIsHost()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var targetParticipantId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = hostId };
        var participant = new TranslationRoomParticipant
        {
            Id = targetParticipantId,
            TranslationRoomId = roomId,
            Status = "CONNECTED",
            Role = TranslationRoomParticipantRole.PARTICIPANT.ToString()
        };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByIdAsync(targetParticipantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        var result = await _sut.KickParticipantAsync(roomId, targetParticipantId, hostId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("KICKED");
        _participantRepositoryMock.Verify(repo => repo.Update(participant), Times.Once);
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeaveRoomAsync_ShouldSetStatusToLeft_WhenParticipantLeaves()
    {
        var roomId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var participant = new TranslationRoomParticipant
        {
            Id = participantId,
            UserId = userId,
            TranslationRoomId = roomId,
            Status = "CONNECTED"
        };

        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        var result = await _sut.LeaveRoomAsync(roomId, userId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("LEFT");
        participant.LeftAt.Should().NotBeNull();
        participant.LeftAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        _participantRepositoryMock.Verify(repo => repo.Update(participant), Times.Once);
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
    /// <summary>
    /// WT-354. A dropped socket used to be recorded by calling LeaveRoomAsync, which writes the
    /// terminal status LEFT — and the People panel hides LEFT rows. A backgrounded tab, a sleeping
    /// laptop or a moment of bad wifi therefore deleted a participant from every roster in the
    /// room while they were still in the call. Production held 182 LEFT rows against 29
    /// DISCONNECTED, which is what a distinction the write path never made looks like from the
    /// outside.
    /// </summary>
    [Fact]
    public async Task MarkParticipantDisconnectedAsync_ShouldSetDisconnected_NotLeft_WhenSocketDrops()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var participant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TranslationRoomId = roomId,
            Status = "CONNECTED"
        };

        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        var result = await _sut.MarkParticipantDisconnectedAsync(roomId, userId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("DISCONNECTED");
        // Not a departure, so nothing may claim they departed.
        participant.LeftAt.Should().BeNull();
        _participantRepositoryMock.Verify(repo => repo.Update(participant), Times.Once);
    }

    /// <summary>
    /// The ordering hazard the guard exists for, and the reason this fix is not a one-line swap:
    /// pressing Leave writes LEFT and THEN closes the socket, so the offline event arrives
    /// immediately afterwards on every clean departure. Without the guard it would rewrite that
    /// LEFT to DISCONNECTED and put the person who just left back on everyone's roster — a worse
    /// bug than the one being fixed, and one that would look like a ghost rather than a mistake.
    /// </summary>
    [Fact]
    public async Task MarkParticipantDisconnectedAsync_ShouldLeaveTerminalStatusAlone_WhenTheSocketClosesAfterLeaving()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var leftAt = DateTime.UtcNow.AddSeconds(-1);

        var participant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TranslationRoomId = roomId,
            Status = "LEFT",
            LeftAt = leftAt
        };

        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        var result = await _sut.MarkParticipantDisconnectedAsync(roomId, userId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("LEFT");
        participant.LeftAt.Should().Be(leftAt);
        _participantRepositoryMock.Verify(repo => repo.Update(It.IsAny<TranslationRoomParticipant>()), Times.Never);
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetParticipantsAsync_ShouldFilterAndSortParticipants()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = hostId };

        var participants = new List<TranslationRoomParticipant>
        {
            new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, DisplayName = "Alice", Status = "CONNECTED", Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(), JoinedAt = DateTime.UtcNow.AddMinutes(-5), ListenLanguage = "en", SpeakLanguage = "en" },
            new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, DisplayName = "Bob", Status = "LEFT", Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(), JoinedAt = DateTime.UtcNow.AddMinutes(-10), ListenLanguage = "vi", SpeakLanguage = "vi" },
            new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, DisplayName = "Charlie", Status = "CONNECTED", Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(), JoinedAt = DateTime.UtcNow.AddMinutes(-2), ListenLanguage = "fr", SpeakLanguage = "fr" }
        };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants[0]); // Requester is host

        _participantRepositoryMock.Setup(repo => repo.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants);

        // Test Filter by Status and SortBy DisplayName Descending
        var request = new GetParticipantsRequest
        {
            Status = "CONNECTED",
            SortBy = "displayname",
            IsDescending = true
        };

        var result = await _sut.GetParticipantsAsync(roomId, request, hostId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value![0].DisplayName.Should().Be("Charlie");
        result.Value![1].DisplayName.Should().Be("Alice");
    }

    // WT-188 — admission is "room host OR workspace Owner/Admin", not host-only.

    [Fact]
    public async Task AdmitParticipantAsync_ShouldAdmit_WhenRequesterIsRoomHost()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = hostId, WorkspaceId = Guid.NewGuid() };
        var participant = new TranslationRoomParticipant
        {
            Id = participantId,
            TranslationRoomId = roomId,
            Status = "WAITING"
        };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByIdAsync(participantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        var result = await _sut.AdmitParticipantAsync(roomId, participantId, hostId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("CONNECTED");
        // The host path must not depend on WorkspaceService being reachable at all.
        _workspaceMemberDirectoryMock.Verify(
            d => d.IsOwnerOrAdminAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AdmitParticipantAsync_ShouldAdmit_WhenRequesterIsWorkspaceOwnerOrAdmin()
    {
        var roomId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var workspaceOwnerId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = hostId, WorkspaceId = workspaceId };
        var participant = new TranslationRoomParticipant
        {
            Id = participantId,
            TranslationRoomId = roomId,
            Status = "WAITING"
        };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByIdAsync(participantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        _workspaceMemberDirectoryMock
            .Setup(d => d.IsOwnerOrAdminAsync(workspaceId, workspaceOwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.AdmitParticipantAsync(roomId, participantId, workspaceOwnerId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be("CONNECTED");
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdmitParticipantAsync_ShouldReturnForbidden_WhenRequesterIsPlainMember()
    {
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid()
        };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        var result = await _sut.AdmitParticipantAsync(roomId, Guid.NewGuid(), Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
