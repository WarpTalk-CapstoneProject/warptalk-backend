using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class Workspace
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public Guid OwnerId { get; set; }

    public string? LogoUrl { get; set; }

    public string PlanTier { get; set; } = null!;

    public string Settings { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public virtual User Owner { get; set; } = null!;

    public virtual ICollection<WorkspaceInvitation> WorkspaceInvitations { get; set; } = new List<WorkspaceInvitation>();
}
