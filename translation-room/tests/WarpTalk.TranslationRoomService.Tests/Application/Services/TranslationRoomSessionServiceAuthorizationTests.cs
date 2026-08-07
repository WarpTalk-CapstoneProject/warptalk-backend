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
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// This whole surface had no authorization: neither TranslationRoomSessionsController nor
/// TranslationRoomSessionService ever asked who was calling, so [Authorize] alone meant any
/// authenticated user could start, mutate or mark ENDED a translation session on ANY room id.
/// UpdateSessionAsync/EndSessionAsync checked only that the session belonged to the room in the
/// route — a condition the caller supplies both halves of, so it prevented nothing.
///
/// Reads use <c>RoomReadAccess.IsReadableBy</c>, writes use <c>RoomHostAccess</c>; these pin both
/// directions of each, because a guard nobody can pass is as much of a demo bug as no guard at all.
/// </summary>
public class TranslationRoomSessionServiceAuthorizationTests
{
    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomSessionRepository> _sessionRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IWorkspaceMemberDirectory> _workspaceMemberDirectory = new();
    private readonly TranslationRoomSessionService _sut;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();

    public TranslationRoomSessionServiceAuthorizationTests()
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_roomRepository.Object);
        _unitOfWork.Setup(u => u.TranslationRoomSessionRepository).Returns(_sessionRepository.Object);

        // Default: the caller holds no workspace privilege, so host identity alone decides.
        _workspaceMemberDirectory
            .Setup(d => d.IsOwnerOrAdminAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _sut = new TranslationRoomSessionService(
            _unitOfWork.Object,
            _workspaceMemberDirectory.Object,
            Mock.Of<ILogger<TranslationRoomSessionService>>());
    }

    // ── Writes: host authority ────────────────────────────────────────────────────

    [Fact]
    public async Task StartSessionAsync_ShouldReturnForbidden_ForAStranger()
    {
        GivenRoom();

        var result = await _sut.StartSessionAsync(RoomId, new CreateTranslationRoomSessionDto("en"), Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        _sessionRepository.Verify(
            r => r.AddAsync(It.IsAny<TranslationRoomSession>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartSessionAsync_ShouldSucceed_ForTheRoomHost()
    {
        GivenRoom();

        var result = await _sut.StartSessionAsync(RoomId, new CreateTranslationRoomSessionDto("en"), HostId);

        result.IsSuccess.Should().BeTrue();
        _sessionRepository.Verify(
            r => r.AddAsync(It.IsAny<TranslationRoomSession>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // WT-188's rule, which the hub and the admission path also follow: the web client grants
    // host-like room controls to workspace Owners/Admins, so host-only here would 403 exactly the
    // people the UI shows the controls to.
    [Fact]
    public async Task StartSessionAsync_ShouldSucceed_ForAWorkspaceOwnerOrAdmin()
    {
        GivenRoom();
        var owner = Guid.NewGuid();
        _workspaceMemberDirectory
            .Setup(d => d.IsOwnerOrAdminAsync(WorkspaceId, owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.StartSessionAsync(RoomId, new CreateTranslationRoomSessionDto("en"), owner);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateSessionAsync_ShouldReturnForbidden_ForAStranger()
    {
        GivenRoom();
        GivenSession();

        var result = await _sut.UpdateSessionAsync(
            RoomId, SessionId, new UpdateTranslationRoomSessionDto("ENDED", null), Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateSessionAsync_ShouldSucceed_ForTheRoomHost()
    {
        GivenRoom();
        var session = GivenSession();

        var result = await _sut.UpdateSessionAsync(
            RoomId, SessionId, new UpdateTranslationRoomSessionDto(null, "https://cdn/audio.wav"), HostId);

        result.IsSuccess.Should().BeTrue();
        session.AudioUrl.Should().Be("https://cdn/audio.wav");
    }

    /// <summary>
    /// The headline of the hole: a stranger could mark another workspace's live meeting ENDED.
    /// </summary>
    [Fact]
    public async Task EndSessionAsync_ShouldReturnForbidden_ForAStranger()
    {
        GivenRoom();
        var session = GivenSession();

        var result = await _sut.EndSessionAsync(RoomId, SessionId, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        session.Status.Should().Be("ACTIVE");
        session.EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task EndSessionAsync_ShouldSucceed_ForTheRoomHost()
    {
        GivenRoom();
        var session = GivenSession();

        var result = await _sut.EndSessionAsync(RoomId, SessionId, HostId);

        result.IsSuccess.Should().BeTrue();
        session.Status.Should().Be("ENDED");
        session.EndedAt.Should().NotBeNull();
    }

    /// <summary>
    /// The refusal must come before the session lookup, so the difference between "not found" and
    /// "does not belong to this room" cannot be used to probe for session ids in rooms the caller
    /// cannot see.
    /// </summary>
    [Fact]
    public async Task EndSessionAsync_ShouldNotLookUpTheSession_WhenTheCallerIsRefused()
    {
        GivenRoom();
        GivenSession();

        await _sut.EndSessionAsync(RoomId, SessionId, Guid.NewGuid());

        _sessionRepository.Verify(
            r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Reads: room-read access ───────────────────────────────────────────────────

    [Fact]
    public async Task GetSessionsAsync_ShouldReturnForbidden_ForAStranger()
    {
        GivenRoomQuery();

        var result = await _sut.GetSessionsAsync(RoomId, Guid.NewGuid(), "stranger@example.com");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        _sessionRepository.Verify(
            r => r.GetSessionsByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSessionsAsync_ShouldSucceed_ForTheRoomHost()
    {
        GivenRoomQuery();
        GivenStoredSessions();

        var result = await _sut.GetSessionsAsync(RoomId, HostId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().HaveCount(1);
    }

    /// <summary>
    /// The three legitimate web callers — the room detail page, the transcript panel's session
    /// bucketing and the AI summaries page — fetch this as ordinary attendees, not as the host.
    /// </summary>
    [Fact]
    public async Task GetSessionsAsync_ShouldSucceed_ForAParticipant()
    {
        var attendee = Guid.NewGuid();
        GivenRoomQuery(participantUserIds: new[] { attendee });
        GivenStoredSessions();

        var result = await _sut.GetSessionsAsync(RoomId, attendee);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// The read predicate is RoomReadAccess, so its invitation clause comes along: a guest who can
    /// already see the room in their list because they were invited by email is not refused here.
    /// </summary>
    [Fact]
    public async Task GetSessionsAsync_ShouldSucceed_ForAnInvitedGuest()
    {
        GivenRoomQuery(invitedEmails: new[] { "guest@example.com" });
        GivenStoredSessions();

        var result = await _sut.GetSessionsAsync(RoomId, Guid.NewGuid(), "guest@example.com");

        result.IsSuccess.Should().BeTrue();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    private TranslationRoom GivenRoom()
    {
        var room = new TranslationRoom { Id = RoomId, HostId = HostId, WorkspaceId = WorkspaceId };
        _roomRepository.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        return room;
    }

    private TranslationRoomSession GivenSession()
    {
        var session = new TranslationRoomSession
        {
            Id = SessionId,
            TranslationRoomId = RoomId,
            MainLanguage = "en",
            Status = "ACTIVE",
            StartedAt = DateTime.UtcNow
        };
        _sessionRepository.Setup(r => r.GetByIdAsync(SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        return session;
    }

    private void GivenStoredSessions() =>
        _sessionRepository
            .Setup(r => r.GetSessionsByRoomIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomSession>
            {
                new()
                {
                    Id = SessionId,
                    TranslationRoomId = RoomId,
                    MainLanguage = "en",
                    Status = "ACTIVE",
                    StartedAt = DateTime.UtcNow
                }
            });

    /// <summary>
    /// The read guard runs RoomReadAccess.IsReadableBy against Query(), so the fixture has to be a
    /// real IQueryable with the navigations populated — a canned bool would test nothing about the
    /// predicate actually in force.
    /// </summary>
    private void GivenRoomQuery(Guid[]? participantUserIds = null, string[]? invitedEmails = null)
    {
        var room = new TranslationRoom
        {
            Id = RoomId,
            HostId = HostId,
            WorkspaceId = WorkspaceId,
            IsActive = true,
            DeletedAt = null,
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

        _roomRepository.Setup(r => r.Query()).Returns(new[] { room }.AsQueryable());
    }
}
