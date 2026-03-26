using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Infrastructure;

public partial class MeetingParticipant
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }

    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = null!;

    public string Role { get; set; } = null!;

    public string ListenLanguage { get; set; } = null!;

    public string SpeakLanguage { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? ConnectionType { get; set; }

    public bool IsMuted { get; set; }

    public bool IsUsingVoiceClone { get; set; }

    public DateTime? JoinedAt { get; set; }

    public DateTime? LeftAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Meeting Meeting { get; set; } = null!;

    public virtual ICollection<MeetingAudioRoute> MeetingAudioRouteSourceParticipants { get; set; } = new List<MeetingAudioRoute>();

    public virtual ICollection<MeetingAudioRoute> MeetingAudioRouteTargetParticipants { get; set; } = new List<MeetingAudioRoute>();
}
