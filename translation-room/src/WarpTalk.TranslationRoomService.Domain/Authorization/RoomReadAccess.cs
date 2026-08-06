using System;
using System.Linq;
using System.Linq.Expressions;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Authorization;

/// <summary>
/// WT-304 — the single definition of "who may READ this room".
///
/// This predicate had been written out by hand in three places that then disagreed:
/// the rooms list/history query (host OR participant OR invited-by-email), the artifacts and
/// feedback guard (host OR participant — no invitation clause), and the participant-list read
/// (host OR workspace Owner/Admin — no invitation clause). WT-188 reconciled them once and WT-313
/// reconciled them again; this is the third drift, so the clause now lives here and the call sites
/// consume it instead of restating it.
///
/// Scope: this type owns the clauses the translation-room database can answer by itself —
/// host, participant, invitation. It deliberately does NOT model workspace Owner/Admin: that
/// answer lives in WorkspaceService behind a gRPC call, cannot appear in an EF expression tree,
/// and applies to only one of the three reads. See
/// <c>TranslationRoomParticipantService.HasRoomHostAuthorityAsync</c>, the WT-313 counterpart of
/// this type for host-adjacent authority.
/// </summary>
public static class RoomReadAccess
{
    /// <summary>
    /// Invitation states that still confer read access.
    ///
    /// An ALLOW-list, not "anything that is not DECLINED". <see cref="TranslationRoomInvitation"/>
    /// has no expiry column and no revoked state today — the only writers set PENDING at creation
    /// and flip it to ACCEPTED when the invitee joins — so the two are exhaustive right now. Stating
    /// it as an allow-list is what keeps it safe later: when a REVOKED or EXPIRED state is added
    /// (WorkspaceService's own invitations already have both), a deny-list would silently keep
    /// granting access to invitations that had just been taken away, whereas this fails closed and
    /// forces whoever adds the state to come here.
    /// </summary>
    public static readonly string[] InvitationStatusesGrantingRead = { "PENDING", "ACCEPTED" };

    /// <summary>
    /// Null when the caller has no usable email claim, so callers can skip the invitation lookup
    /// entirely rather than probing the database with an empty string (which would match nothing,
    /// but only by luck).
    /// </summary>
    public static string? NormalizeEmail(string? email)
        => string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    /// <summary>
    /// The room-level read predicate, safe to hand to EF as a <c>Where</c>/<c>Any</c> clause.
    ///
    /// Email comparison is deliberately exact rather than case-insensitive: that is what the rooms
    /// list has always done, and loosening it here would widen the boundary for every consumer at
    /// once, under cover of a bug fix. It is a known sharp edge — an invitation typed
    /// "User@x.com" does not match a "user@x.com" token — but it is pre-existing, and it is now at
    /// least wrong in exactly one place instead of three.
    /// </summary>
    public static Expression<Func<TranslationRoom, bool>> IsReadableBy(Guid userId, string? userEmail)
    {
        var email = NormalizeEmail(userEmail);

        if (email is null)
        {
            return room =>
                room.HostId == userId
                || room.TranslationRoomParticipants.Any(p => p.UserId == userId);
        }

        return room =>
            room.HostId == userId
            || room.TranslationRoomParticipants.Any(p => p.UserId == userId)
            || room.TranslationRoomInvitations.Any(i =>
                i.Email == email
                && InvitationStatusesGrantingRead.Contains(i.Status));
    }

    /// <summary>
    /// The invitation clause on its own, for the one caller that has already resolved the host and
    /// participant clauses and must not re-ask the database for them.
    /// <see cref="TranslationRoomParticipantService"/>'s participant-list read is polled every three
    /// seconds by the waiting page, so it checks the cheap in-memory answers first and only reaches
    /// this lookup for a caller that is neither a participant nor a workspace Owner/Admin.
    /// </summary>
    public static Expression<Func<TranslationRoomInvitation, bool>> GrantsReadOfRoom(Guid translationRoomId, string email)
        => invitation =>
            invitation.TranslationRoomId == translationRoomId
            && invitation.Email == email
            && InvitationStatusesGrantingRead.Contains(invitation.Status);
}
