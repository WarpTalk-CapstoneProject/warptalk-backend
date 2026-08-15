using System;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceMemberMapper
{
    /// <summary>
    /// A newly created member may create meetings. Stated here, in the code, on purpose.
    ///
    /// The column carries <c>DEFAULT true</c> in Postgres and the EF model used to declare that
    /// default too — and that pairing is exactly what broke it. <c>HasDefaultValue(true)</c> makes
    /// <c>true</c> the property's EF *sentinel*, and EF omits a column from the INSERT only when the
    /// property still equals its sentinel. Neither factory below assigned the property, so it held
    /// the CLR default <c>false</c>, which is not the sentinel — EF wrote <c>false</c> explicitly and
    /// the database default never applied. Declaring the default as <c>true</c> is what persisted
    /// <c>false</c>.
    ///
    /// The consequence was that the workspace Owner (<see cref="CreateOwnerMember"/>) and everyone
    /// who joined by accepting an invitation (<see cref="CreateInvitationMember"/>) were refused
    /// meeting creation with a 403, in a workspace they had just created or been invited to, while
    /// a member whose flag had been toggled by hand could create them.
    /// </summary>
    private const bool CanCreateMeetingsOnJoin = true;

    /// <summary>
    /// WT-371 #2: whether a member who has just joined may open meetings, decided by membership
    /// type rather than granted to everyone.
    ///
    /// <see cref="CanCreateMeetingsOnJoin"/> above was applied unconditionally, including to people
    /// accepting an invitation as <see cref="MembershipType.External"/>. An external collaborator —
    /// someone whose email does not match a verified workspace domain — therefore landed in the
    /// workspace able to create meetings in it, which is the one thing that spends the tenant's
    /// credits. Every other external restriction in the product already keys off this same column's
    /// sibling: <c>DocumentAccessEvaluator</c> denies External by default and admits it only through
    /// an explicit policy row or a meeting they actually attended.
    ///
    /// External starts at false and is GRANTED, not started at true and revoked. The Members page
    /// already carries the per-member toggle that grants it, so an Owner who does want a specific
    /// guest to host has a one-click path; there was no path back from "every guest can host".
    ///
    /// Unknown or missing values are treated as Internal. The column is written by
    /// <c>WorkspaceHelper.DetermineMembershipTypeAsync</c>, which only ever produces the two enum
    /// names, and a workspace with no verified-domain policy classifies everyone Internal — so
    /// defaulting the other way would silently strip meeting creation from ordinary members of
    /// every non-enterprise workspace.
    /// </summary>
    private static bool CanCreateMeetingsFor(string membershipType) =>
        !string.Equals(membershipType, MembershipType.External.ToString(), StringComparison.OrdinalIgnoreCase);

    public static WorkspaceMember CreateOwnerMember(Guid workspaceId, Guid userId, Guid roleId, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        return new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            Status = WorkspaceMemberStatus.Active.ToStorageValue(),
            MembershipType = MembershipType.Internal.ToString(),
            CanCreateMeetings = CanCreateMeetingsOnJoin,
            JoinedAt = now
        };
    }

    public static WorkspaceMember CreateInvitationMember(Guid workspaceId, Guid userId, Guid roleId, string membershipType, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        return new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            Status = WorkspaceMemberStatus.Active.ToStorageValue(),
            MembershipType = membershipType,
            CanCreateMeetings = CanCreateMeetingsFor(membershipType),
            JoinedAt = now
        };
    }

    /// <summary>
    /// Bring a departed member's row back as a fresh membership. WT-416.
    ///
    /// WHY THIS EXISTS INSTEAD OF A SECOND INSERT
    ///     Leaving a workspace is a SOFT delete — the row stays and RemovedAt is stamped — but
    ///     workspace_members carries
    ///
    ///         UNIQUE (workspace_id, user_id)
    ///
    ///     with no `WHERE removed_at IS NULL` predicate. So the schema says one row per person
    ///     per workspace FOREVER, while the code says a person may join more than once and tells
    ///     the difference by RemovedAt. Approving a rejoin inserted a second row for the same
    ///     pair, hit the constraint, and surfaced as a 500 with "An unexpected error occurred".
    ///     Three members of one production workspace were stuck outside it.
    ///
    /// Every field CreateInvitationMember sets is set here too, from the same helpers, so the
    /// two cannot drift into "a rejoining member gets different defaults from a new one" — which
    /// would be a subtler bug than the crash this replaces. RemovedBy is cleared alongside
    /// RemovedAt: a row that is live again must not still name who removed it.
    /// </summary>
    public static void ReviveAsMember(
        this WorkspaceMember member, Guid roleId, string membershipType, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        member.RoleId = roleId;
        member.Status = WorkspaceMemberStatus.Active.ToStorageValue();
        member.MembershipType = membershipType;
        member.CanCreateMeetings = CanCreateMeetingsFor(membershipType);
        member.JoinedAt = now;
        member.RemovedAt = null;
        member.RemovedBy = null;
    }

    public static WorkspaceMemberDto ToDto(this WorkspaceMember member, string fullName, string email, string? avatarUrl, string roleName)
    {
        return new WorkspaceMemberDto(
            member.Id,
            member.WorkspaceId,
            member.UserId,
            fullName,
            email,
            avatarUrl,
            roleName,
            member.Status,
            member.JoinedAt,
            member.MembershipType,
            member.CanCreateMeetings
        );
    }
}
