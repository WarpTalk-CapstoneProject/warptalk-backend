using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class WorkspaceInvitation
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string Email { get; set; } = null!;

    public Guid RoleId { get; set; }

    public Guid InvitedBy { get; set; }

    public string Token { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User InvitedByNavigation { get; set; } = null!;

    public virtual Role Role { get; set; } = null!;

    public virtual Workspace Workspace { get; set; } = null!;
}
