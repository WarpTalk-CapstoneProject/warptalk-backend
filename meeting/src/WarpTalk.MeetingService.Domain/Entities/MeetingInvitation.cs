using System;

namespace WarpTalk.MeetingService.Domain.Entities;

public partial class MeetingInvitation
{
    public Guid Id { get; set; }

    public Guid MeetingRoomId { get; set; }

    public Guid? InviteeUserId { get; set; }

    public string? InviteeEmail { get; set; }

    public Guid? GroupId { get; set; }

    public Guid WorkspaceId { get; set; }

    // Status: PENDING, ACCEPTED, DECLINED, REVOKED
    public string Status { get; set; } = "PENDING";

    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual MeetingRoom MeetingRoom { get; set; } = null!;
}
