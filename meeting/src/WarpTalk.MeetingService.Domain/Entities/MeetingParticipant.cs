using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Domain.Entities;

public class MeetingParticipant
{
    public Guid Id { get; set; }

    public Guid MeetingRoomId { get; set; }

    public Guid? UserId { get; set; }

    public string ProviderIdentity { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public DateTime? JoinedAt { get; set; }

    public DateTime? LeftAt { get; set; }

    public virtual MeetingRoom MeetingRoom { get; set; } = null!;

    public virtual ICollection<MeetingTrack> MeetingTracks { get; set; } = new List<MeetingTrack>();
}
