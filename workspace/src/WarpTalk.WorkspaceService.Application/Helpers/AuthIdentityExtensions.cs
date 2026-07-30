using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class AuthIdentityExtensions
{
    public static async Task<Guid?> GetRoleIdByNameAsync(this IAuthIdentityClient authIdentity, string roleName, CancellationToken ct)
    {
        if (authIdentity == null) throw new ArgumentNullException(nameof(authIdentity));
        var role = await authIdentity.GetRoleByNameAsync(roleName, ct);
        return role?.Id;
    }

    public static async Task<string> GetRoleNameByIdAsync(this IAuthIdentityClient authIdentity, Guid roleId, CancellationToken ct)
    {
        if (authIdentity == null) throw new ArgumentNullException(nameof(authIdentity));
        var role = await authIdentity.GetRoleByIdAsync(roleId, ct);
        return role?.Name ?? "Member";
    }
}
