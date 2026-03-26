using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Infrastructure;

public partial class MeetingAudioRoute
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }

    public Guid SourceParticipantId { get; set; }

    public Guid TargetParticipantId { get; set; }

    public string SourceLanguage { get; set; } = null!;

    public string TargetLanguage { get; set; } = null!;

    public bool VoiceCloneEnabled { get; set; }

    public string? StreamId { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual Meeting Meeting { get; set; } = null!;

    public virtual MeetingParticipant SourceParticipant { get; set; } = null!;

    public virtual MeetingParticipant TargetParticipant { get; set; } = null!;
}
