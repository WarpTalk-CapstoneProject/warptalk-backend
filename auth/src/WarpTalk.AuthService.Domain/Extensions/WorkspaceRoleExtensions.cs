using System;
using WarpTalk.AuthService.Domain.Enums;

namespace WarpTalk.AuthService.Domain.Extensions;

public static class WorkspaceUserRoleExtensions
{
    public static string ToRoleName(this WorkspaceUserRole role) => role switch
    {
        WorkspaceUserRole.Owner => "Owner",
        WorkspaceUserRole.Admin => "Admin",
        WorkspaceUserRole.Member => "Member",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    public static WorkspaceUserRole ToWorkspaceUserRole(this string? roleName) => roleName switch
    {
        "Owner" => WorkspaceUserRole.Owner,
        "Admin" => WorkspaceUserRole.Admin,
        "Member" => WorkspaceUserRole.Member,
        _ => WorkspaceUserRole.Member
    };
}
