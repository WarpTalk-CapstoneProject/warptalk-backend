using System.Collections.Generic;
using System.Linq;
using System;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Helpers;

public static class UserExtensions
{
    /// <summary>
    /// The legacy platform roles an account holds, for the token's role claims.
    ///
    /// Two G10 changes. Revoked assignments no longer count — the old query read every
    /// auth.user_roles row, so a revoked role stayed in every token forever. And the platform
    /// role 'admin' is never taken from here: whether a token says "admin" is decided by
    /// auth.staff_members (see AuthResponseHelper), so a person whose staff access was removed
    /// stops carrying it at their next refresh even if a legacy row survived somewhere.
    /// </summary>
    public static List<string> GetRoles(this User user, string defaultRole)
    {
        var roles = user.UserRoleUsers?
            .Where(ur => ur.RevokedAt is null)
            .Select(ur => ur.Role?.Name ?? defaultRole)
            .Where(name => !string.Equals(name, StaffConstants.LegacyAdminRoleName, StringComparison.Ordinal))
            .Distinct()
            .ToList() ?? new List<string>();

        if (roles.Count == 0)
        {
            roles.Add(defaultRole);
        }

        return roles;
    }
}
