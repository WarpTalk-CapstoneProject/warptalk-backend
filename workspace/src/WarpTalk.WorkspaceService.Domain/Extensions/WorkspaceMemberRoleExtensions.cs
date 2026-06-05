using System;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Domain.Extensions;

public static class WorkspaceMemberRoleExtensions
{
    public static string ToRoleName(this WorkspaceMemberRole role) => role switch
    {
        WorkspaceMemberRole.Owner => "Owner",
        WorkspaceMemberRole.Admin => "Admin",
        WorkspaceMemberRole.Member => "Member",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    public static WorkspaceMemberRole ToWorkspaceMemberRole(this string? roleName) => roleName switch
    {
        "Owner" => WorkspaceMemberRole.Owner,
        "Admin" => WorkspaceMemberRole.Admin,
        "Member" => WorkspaceMemberRole.Member,
        _ => WorkspaceMemberRole.Member
    };

    public static bool IsOwner(this string? roleName)
    {
        return roleName == WorkspaceMemberRole.Owner.ToRoleName();
    }

    public static bool IsAdmin(this string? roleName)
    {
        return roleName == WorkspaceMemberRole.Admin.ToRoleName();
    }

    public static bool IsMember(this string? roleName)
    {
        return roleName == WorkspaceMemberRole.Member.ToRoleName();
    }

    public static bool IsOwnerOrAdmin(this string? roleName)
    {
        return roleName == WorkspaceMemberRole.Owner.ToRoleName() || roleName == WorkspaceMemberRole.Admin.ToRoleName();
    }
}
