using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Domain.Entities;

public partial class MeetingParticipant
{
    public Guid Id { get; set; }

    public Guid MeetingRoomId { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>The LiveKit identity. This is the user id — it is NOT a name (WT-356).</summary>
    public string ProviderIdentity { get; set; } = null!;

    /// <summary>
    /// WT-356: the participant's human-readable name, resolved once at join and stored so chat
    /// and the video tile agree. Null for rows written before this column existed.
    /// </summary>
    public string? DisplayName { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public DateTime? JoinedAt { get; set; }

    public DateTime? LeftAt { get; set; }

    public virtual ICollection<MeetingChatMessage> MeetingChatMessages { get; set; } = new List<MeetingChatMessage>();

    public virtual MeetingRoom MeetingRoom { get; set; } = null!;

    public virtual ICollection<MeetingTrack> MeetingTracks { get; set; } = new List<MeetingTrack>();
}
