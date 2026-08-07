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
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-334. <c>GET /translation-rooms/{id}</c> had no authorization at all: the controller's
/// class-level <c>[Authorize]</c> was the entire check, and the service method took no user id, so
/// any authenticated user could read any room in any workspace — title, description, room code,
/// schedule, settings, host.
///
/// The guard is WT-304's <c>RoomReadAccess.IsReadableBy</c>, so these pin BOTH directions: every
/// legitimate reader (host, participant, invited-by-email) still gets the room, and a stranger
/// gets NotFound. A guard nobody can pass would be as much of a regression as no guard — the room
/// detail page is on the join path.
///
/// The refusal assertions deliberately check <see cref="ErrorCodes.NotFound"/> and the not-found
/// MESSAGE, not just "failed": returning Forbidden here would confirm that a room with this id
/// exists, which is the cross-tenant leak the ticket is about. If someone later "improves" the
/// error to a 403, <see cref="ReadDetail_RefusalIsIndistinguishableFromAMissingRoom"/> fails.
/// </summary>
public class TranslationRoomDetailReadAuthorizationTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepository = new();
    private readonly Mock<IWorkspaceMemberDirectory> _workspaceMemberDirectory = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _sut;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    private const string InvitedEmail = "invited@example.com";

    public TranslationRoomDetailReadAuthorizationTests()
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_roomRepository.Object);
        _unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participantRepository.Object);

        _participantRepository
            .Setup(p => p.CountSeatHoldingParticipantsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _sut = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _unitOfWork.Object,
            Mock.Of<ILanguagePolicy>(),
            Mock.Of<IAudioRouteEventProcessor>(),
            Mock.Of<ITranslationRoomAudioRouteService>(),
            Mock.Of<IUserSettingsDirectory>(),
            Mock.Of<IWorkspaceMeetingPolicy>(),
            // Not an Owner/Admin of anything. The detail read also admits a workspace
            // Owner/Admin — it has to, because the rooms list shows them every room in the
            // workspace — and these cases are about the personal predicate, so this substitute
            // deliberately answers false and leaves that branch to its own test below.
            _workspaceMemberDirectory.Object,
            Mock.Of<WarpTalk.Shared.Interfaces.IEmailService>(),
            Mock.Of<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>());
    }

    // ── The legitimate readers: all three must still work ─────────────────────────

    [Fact]
    public async Task ReadDetail_ShouldSucceed_ForTheRoomHost()
    {
        GivenRoom();

        var result = await _sut.GetTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be(RoomId);
    }

    [Fact]
    public async Task ReadDetail_ShouldSucceed_ForAParticipant()
    {
        var participantId = Guid.NewGuid();
        GivenRoom(participantUserIds: new[] { participantId });

        var result = await _sut.GetTranslationRoomAsync(RoomId, participantId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be(RoomId);
    }

    /// <summary>
    /// The clause most likely to be dropped when someone re-spells this predicate: an invitee has
    /// no participant row yet. They reach the room detail page from the invitation link BEFORE
    /// joining, so losing this clause breaks the invite flow rather than merely narrowing a read.
    /// </summary>
    [Fact]
    public async Task ReadDetail_ShouldSucceed_ForAUserInvitedByEmail()
    {
        GivenRoom(invitedEmails: new[] { InvitedEmail });

        var result = await _sut.GetTranslationRoomAsync(RoomId, Guid.NewGuid(), InvitedEmail);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be(RoomId);
    }

    // ── The refusal ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The hole itself: before WT-334 this returned the full room. An authenticated stranger from
    /// another tenant is exactly the caller the endpoint served happily.
    /// </summary>
    [Fact]
    public async Task ReadDetail_ShouldReturnNotFound_ForAnUnrelatedAuthenticatedUser()
    {
        GivenRoom();

        var result = await _sut.GetTranslationRoomAsync(RoomId, Guid.NewGuid(), "stranger@example.com");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>
    /// 404-not-403, asserted as a property rather than a spelling: the refusal for a room that
    /// EXISTS must be byte-identical to the refusal for a room that does not, or the response
    /// confirms the id to a prober.
    /// </summary>
    [Fact]
    public async Task ReadDetail_RefusalIsIndistinguishableFromAMissingRoom()
    {
        GivenRoom();
        var stranger = Guid.NewGuid();

        var refused = await _sut.GetTranslationRoomAsync(RoomId, stranger, null);

        // A room id that genuinely does not exist.
        var absentRoomId = Guid.NewGuid();
        _roomRepository
            .Setup(r => r.GetByIdAsync(absentRoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);
        var missing = await _sut.GetTranslationRoomAsync(absentRoomId, stranger, null);

        refused.IsSuccess.Should().BeFalse();
        missing.IsSuccess.Should().BeFalse();
        refused.ErrorCode.Should().Be(missing.ErrorCode);
        refused.Error.Should().Be(missing.Error);
    }

    /// <summary>
    /// A plain workspace member is not a room reader. <c>RoomReadAccess</c> deliberately does not
    /// model workspace roles, and the rooms LIST has never admitted them either — this keeps the
    /// detail read from quietly becoming "anyone in the tenant".
    /// </summary>
    [Fact]
    public async Task ReadDetail_ShouldReturnNotFound_ForSomeoneOnlyInvitedToADifferentRoom()
    {
        GivenRoom(invitedEmails: new[] { InvitedEmail });

        var result = await _sut.GetTranslationRoomAsync(RoomId, Guid.NewGuid(), "someone.else@example.com");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>
    /// A DECLINED invitation is not a standing invitation. <c>InvitationStatusesGrantingRead</c> is
    /// an allow-list for this reason; a deny-list would keep granting reads on any status added
    /// later.
    /// </summary>
    [Fact]
    public async Task ReadDetail_ShouldReturnNotFound_WhenTheInvitationIsDeclined()
    {
        GivenRoom(invitedEmails: new[] { InvitedEmail }, invitationStatus: "DECLINED");

        var result = await _sut.GetTranslationRoomAsync(RoomId, Guid.NewGuid(), InvitedEmail);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The guard runs <c>RoomReadAccess.IsReadableBy</c> against <c>Query()</c>, so the fixture is a
    /// real IQueryable with the navigations populated — a canned bool would assert nothing about
    /// the predicate actually in force. <c>GetByIdAsync</c> is stubbed separately because the
    /// method fetches the entity before it authorizes.
    /// </summary>
    private void GivenRoom(
        Guid[]? participantUserIds = null,
        string[]? invitedEmails = null,
        string invitationStatus = "PENDING")
    {
        var room = new TranslationRoom
        {
            Id = RoomId,
            HostId = HostId,
            WorkspaceId = WorkspaceId,
            Title = "Quarterly planning",
            Status = "SCHEDULED",
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
                    Status = invitationStatus
                })
                .ToList()
        };

        _roomRepository
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _roomRepository.Setup(r => r.Query()).Returns(new[] { room }.AsQueryable());
    }
}
