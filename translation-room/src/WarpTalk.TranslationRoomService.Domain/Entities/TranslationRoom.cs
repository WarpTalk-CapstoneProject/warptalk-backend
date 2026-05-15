using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// Room lifecycle:
/// SCHEDULED -> WAITING
/// SCHEDULED -> CANCELLED
/// SCHEDULED -> EXPIRED
/// WAITING -> IN_PROGRESS
/// WAITING -> CANCELLED
/// WAITING -> EXPIRED
/// IN_PROGRESS -> PAUSED
/// PAUSED -> IN_PROGRESS
/// IN_PROGRESS -> ENDED
/// 
/// Draft room is not persisted. If the user discards a draft, no room record is created.
/// </summary>
public partial class TranslationRoom
{
    public Guid Id { get; set; }

    /// <summary>
    /// External AuthService workspace id. No physical FK.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid HostId { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public string TranslationRoomCode { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string TranslationRoomType { get; set; } = null!;

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

    public virtual ICollection<TranslationRoomAudioRoute> TranslationRoomAudioRoutes { get; set; } = new List<TranslationRoomAudioRoute>();

    public virtual ICollection<TranslationRoomFeedback> TranslationRoomFeedbacks { get; set; } = new List<TranslationRoomFeedback>();

    public virtual ICollection<TranslationRoomParticipant> TranslationRoomParticipants { get; set; } = new List<TranslationRoomParticipant>();

    public virtual ICollection<TranslationRoomRecording> TranslationRoomRecordings { get; set; } = new List<TranslationRoomRecording>();

    public virtual TranslationRoomSummary? TranslationRoomSummary { get; set; }
}
