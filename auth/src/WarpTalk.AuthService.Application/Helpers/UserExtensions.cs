using System.Collections.Generic;
using System.Linq;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Helpers;

public static class UserExtensions
{
    public static List<string> GetRoles(this User user, string defaultRole)
    {
        var roles = user.UserRoleUsers?
            .Select(ur => ur.Role?.Name ?? defaultRole)
            .Distinct()
            .ToList() ?? new List<string>();

        if (roles.Count == 0)
        {
            roles.Add(defaultRole);
        }

        return roles;
    }
}
