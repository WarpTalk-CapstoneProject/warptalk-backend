using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class User
{
    public List<string> GetRoles(string defaultRole)
    {
        var roles = UserRoleUsers?
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
