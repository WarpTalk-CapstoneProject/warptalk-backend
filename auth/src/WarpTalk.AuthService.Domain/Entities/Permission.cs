using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class Permission
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    public string? Description { get; set; }

    public string GroupName { get; set; } = null!;

    public virtual ICollection<Role> Roles { get; set; } = new List<Role>();
}
