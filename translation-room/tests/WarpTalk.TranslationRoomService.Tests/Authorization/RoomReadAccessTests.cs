using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Domain.Authorization;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

// NOTE: deliberately NOT namespaced ...Tests.Domain.Authorization — a "Tests.Domain"
// namespace shadows the real WarpTalk.TranslationRoomService.Domain for every sibling test file
// that refers to it relatively (e.g. Application/Mappers/MeetingTypeDefaultsTests.cs).
namespace WarpTalk.TranslationRoomService.Tests.Authorization;

/// <summary>
/// WT-304 — the room-read predicate itself, exercised as a predicate rather than through any one
/// of its three call sites. The point of extracting it was that the three sites had drifted; these
/// tests pin the shared answer so a future edit to any one caller cannot quietly re-fork it.
/// </summary>
public class RoomReadAccessTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private const string InviteeEmail = "invitee@warptalk.vn";

    private static TranslationRoom BuildRoom(
        Guid hostId,
        IEnumerable<Guid>? participantUserIds = null,
        IEnumerable<(string Email, string Status)>? invitations = null)
    {
        return new TranslationRoom
        {
            Id = RoomId,
            HostId = hostId,
            WorkspaceId = Guid.NewGuid(),
            IsActive = true,
            TranslationRoomParticipants = (participantUserIds ?? Array.Empty<Guid>())
                .Select(id => new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = RoomId, UserId = id })
                .ToList(),
            TranslationRoomInvitations = (invitations ?? Array.Empty<(string, string)>())
                .Select(i => new TranslationRoomInvitation { Id = Guid.NewGuid(), TranslationRoomId = RoomId, Email = i.Email, Status = i.Status })
                .ToList()
        };
    }

    private static bool CanRead(TranslationRoom room, Guid userId, string? email)
        => new[] { room }.AsQueryable().Any(RoomReadAccess.IsReadableBy(userId, email));

    [Fact]
    public void Host_CanRead()
    {
        var hostId = Guid.NewGuid();
        CanRead(BuildRoom(hostId), hostId, "host@warptalk.vn").Should().BeTrue();
    }

    [Fact]
    public void Participant_CanRead()
    {
        var userId = Guid.NewGuid();
        var room = BuildRoom(Guid.NewGuid(), participantUserIds: new[] { userId });
        CanRead(room, userId, "member@warptalk.vn").Should().BeTrue();
    }

    // The WT-304 regression: invited by email, never joined, so no participant row exists.
    [Theory]
    [InlineData("PENDING")]
    [InlineData("ACCEPTED")]
    public void InvitedByEmail_WithLiveInvitation_CanRead(string status)
    {
        var room = BuildRoom(Guid.NewGuid(), invitations: new[] { (InviteeEmail, status) });
        CanRead(room, Guid.NewGuid(), InviteeEmail).Should().BeTrue();
    }

    // The allow-list, from the other side: a state outside it grants nothing. DECLINED is the only
    // such state the entity documents today; REVOKED/EXPIRED stand in for the states
    // WorkspaceService's invitations already have and this one will likely grow.
    [Theory]
    [InlineData("DECLINED")]
    [InlineData("REVOKED")]
    [InlineData("EXPIRED")]
    public void InvitedByEmail_WithDeadInvitation_CannotRead(string status)
    {
        var room = BuildRoom(Guid.NewGuid(), invitations: new[] { (InviteeEmail, status) });
        CanRead(room, Guid.NewGuid(), InviteeEmail).Should().BeFalse();
    }

    [Fact]
    public void UnrelatedUser_CannotRead()
    {
        var room = BuildRoom(Guid.NewGuid(), invitations: new[] { (InviteeEmail, "PENDING") });
        CanRead(room, Guid.NewGuid(), "stranger@warptalk.vn").Should().BeFalse();
    }

    // A token with no email claim must not accidentally match an invitation row; the predicate drops
    // the clause entirely rather than comparing against null/empty.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoEmailClaim_FallsBackToHostOrParticipantOnly(string? email)
    {
        var room = BuildRoom(Guid.NewGuid(), invitations: new[] { (InviteeEmail, "PENDING") });
        CanRead(room, Guid.NewGuid(), email).Should().BeFalse();

        var participantId = Guid.NewGuid();
        var joined = BuildRoom(Guid.NewGuid(), participantUserIds: new[] { participantId });
        CanRead(joined, participantId, email).Should().BeTrue();
    }

    [Fact]
    public void EmailClaim_IsTrimmedBeforeMatching()
    {
        var room = BuildRoom(Guid.NewGuid(), invitations: new[] { (InviteeEmail, "PENDING") });
        CanRead(room, Guid.NewGuid(), $"  {InviteeEmail}  ").Should().BeTrue();
    }

    [Fact]
    public void InvitationToSomeoneElse_DoesNotLeakTheRoom()
    {
        var room = BuildRoom(Guid.NewGuid(), invitations: new[] { ("someone.else@warptalk.vn", "PENDING") });
        CanRead(room, Guid.NewGuid(), InviteeEmail).Should().BeFalse();
    }

    [Fact]
    public void InvitationStatusAllowList_IsExactlyPendingAndAccepted()
    {
        RoomReadAccess.InvitationStatusesGrantingRead.Should().BeEquivalentTo(new[] { "PENDING", "ACCEPTED" });
    }
}
