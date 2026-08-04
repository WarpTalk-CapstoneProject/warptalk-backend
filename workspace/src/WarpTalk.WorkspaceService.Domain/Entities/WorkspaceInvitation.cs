using System;
using System.Collections.Generic;
using WarpTalk.WorkspaceService.Domain.Enums;

namespace WarpTalk.WorkspaceService.Domain.Entities;

public partial class WorkspaceInvitation
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string Email { get; set; } = null!;

    public Guid RoleId { get; set; }

    public string MembershipType { get; set; } = null!;

    public Guid? MatchedDomainId { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid InvitedBy { get; set; }

    public Guid? RequestedBy { get; set; }

    public Guid? ReviewedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public string? TokenHash { get; set; }

    public string Status { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public string DeliveryStatus { get; set; } = null!;

    public string? ProviderMessageId { get; set; }

    public DateTime? LastSentAt { get; set; }

    public int SentCount { get; set; }

    public virtual Workspace Workspace { get; set; } = null!;
}
