using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class UserRole
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    /// <summary>
    /// Internal AuthService workspace reference. Nullable for global roles.
    /// </summary>
    public Guid? WorkspaceId { get; set; }

    public DateTime AssignedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? AssignedBy { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? RevokedBy { get; set; }

    public virtual User? AssignedByNavigation { get; set; }

    public virtual User? RevokedByNavigation { get; set; }

    public virtual Role Role { get; set; } = null!;

    public virtual User User { get; set; } = null!;

    public virtual Workspace? Workspace { get; set; }
}
