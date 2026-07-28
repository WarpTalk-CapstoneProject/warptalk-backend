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

    public static WorkspaceMemberRole ToWorkspaceMemberRole(this string? roleName)
    {
        if (string.Equals(roleName, WorkspaceMemberRole.Owner.ToRoleName(), StringComparison.OrdinalIgnoreCase))
            return WorkspaceMemberRole.Owner;
        if (string.Equals(roleName, WorkspaceMemberRole.Admin.ToRoleName(), StringComparison.OrdinalIgnoreCase))
            return WorkspaceMemberRole.Admin;
        return WorkspaceMemberRole.Member;
    }

    public static bool IsOwner(this string? roleName)
    {
        return string.Equals(roleName, WorkspaceMemberRole.Owner.ToRoleName(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAdmin(this string? roleName)
    {
        return string.Equals(roleName, WorkspaceMemberRole.Admin.ToRoleName(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMember(this string? roleName)
    {
        return string.Equals(roleName, WorkspaceMemberRole.Member.ToRoleName(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOwnerOrAdmin(this string? roleName)
    {
        return roleName.IsOwner() || roleName.IsAdmin();
    }
}
