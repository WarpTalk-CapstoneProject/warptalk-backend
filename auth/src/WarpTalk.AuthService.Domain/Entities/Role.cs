using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class Role
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsSystem { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? DeletedBy { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual User? DeletedByNavigation { get; set; }

    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();

    public virtual User? UpdatedByNavigation { get; set; }

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public virtual ICollection<WorkspaceInvitation> WorkspaceInvitations { get; set; } = new List<WorkspaceInvitation>();

    public virtual ICollection<WorkspaceMember> WorkspaceMembers { get; set; } = new List<WorkspaceMember>();
}
