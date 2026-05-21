using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class Workspace
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid OwnerId { get; set; }

    public string? LogoUrl { get; set; }

    public string PlanTier { get; set; } = null!;

    public string Settings { get; set; } = null!;

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

    public virtual User Owner { get; set; } = null!;

    public virtual User? UpdatedByNavigation { get; set; }

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public virtual ICollection<WorkspaceInvitation> WorkspaceInvitations { get; set; } = new List<WorkspaceInvitation>();

    public virtual ICollection<WorkspaceMember> WorkspaceMembers { get; set; } = new List<WorkspaceMember>();
}
