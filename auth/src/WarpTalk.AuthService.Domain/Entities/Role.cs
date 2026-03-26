using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class Role
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsSystem { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public virtual ICollection<WorkspaceInvitation> WorkspaceInvitations { get; set; } = new List<WorkspaceInvitation>();

    public virtual ICollection<Permission> Permissions { get; set; } = new List<Permission>();
}
