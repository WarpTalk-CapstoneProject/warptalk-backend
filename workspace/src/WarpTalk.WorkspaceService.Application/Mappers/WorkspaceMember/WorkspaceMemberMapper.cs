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
            CanCreateMeetings = CanCreateMeetingsOnJoin,
            JoinedAt = now
        };
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
