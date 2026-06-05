using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace WarpTalk.WorkspaceService.Domain.Entities;

public partial class WorkspaceInvitation
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string Email { get; set; } = null!;

    public Guid RoleId { get; set; }

    public string MembershipType { get; set; } = null!;

    public Guid? MatchedDomainId { get; set; }

    public Guid InvitedBy { get; set; }

    public string TokenHash { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Workspace Workspace { get; set; } = null!;
}
