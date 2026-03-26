using System;
using System.Collections.Generic;

namespace WarpTalk.MeetingService.Infrastructure;

public partial class Meeting
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid HostId { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public string MeetingCode { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string MeetingType { get; set; } = null!;

    public int MaxParticipants { get; set; }

    public string SourceLanguage { get; set; } = null!;

    public string TargetLanguages { get; set; } = null!;

    public string Settings { get; set; } = null!;

    public DateTime? ScheduledAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public int? DurationSeconds { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public virtual ICollection<MeetingAudioRoute> MeetingAudioRoutes { get; set; } = new List<MeetingAudioRoute>();

    public virtual ICollection<MeetingFeedback> MeetingFeedbacks { get; set; } = new List<MeetingFeedback>();

    public virtual ICollection<MeetingParticipant> MeetingParticipants { get; set; } = new List<MeetingParticipant>();

    public virtual ICollection<MeetingRecording> MeetingRecordings { get; set; } = new List<MeetingRecording>();

    public virtual MeetingSummary? MeetingSummary { get; set; }
}
