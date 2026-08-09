using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Domain.Entities;

public partial class MeetingRoom
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    public Guid WorkspaceId { get; set; }

    public int MaxQuota { get; set; }

    public int UsedToken { get; set; }

    public string ProviderRoomName { get; set; } = null!;

    public Guid? ActiveHostId { get; set; }

    public string Status { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public DateTime? EndedAt { get; set; }

    // WT-04: host-only room controls.
    public bool IsLocked { get; set; }

    public bool MuteOnEntry { get; set; }

    // WT-06: LiveKit Egress id for the in-progress RoomComposite recording, if any.
    public string? ActiveEgressId { get; set; }

    public virtual ICollection<MeetingChatMessage> MeetingChatMessages { get; set; } = new List<MeetingChatMessage>();

    public virtual ICollection<MeetingParticipant> MeetingParticipants { get; set; } = new List<MeetingParticipant>();

    public virtual ICollection<MeetingInvitation> MeetingInvitations { get; set; } = new List<MeetingInvitation>();
}
