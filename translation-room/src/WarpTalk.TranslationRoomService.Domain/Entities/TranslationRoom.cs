using System;
using System.Collections.Generic;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// Room lifecycle:
/// SCHEDULED -&gt; WAITING
/// SCHEDULED -&gt; CANCELLED
/// SCHEDULED -&gt; EXPIRED
/// WAITING -&gt; IN_PROGRESS
/// WAITING -&gt; CANCELLED
/// WAITING -&gt; EXPIRED
/// IN_PROGRESS -&gt; PAUSED
/// PAUSED -&gt; IN_PROGRESS
/// IN_PROGRESS -&gt; ENDED
/// 
/// Draft room is not persisted. If the user discards a draft, no room record is created.
/// 
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

    public RoomStatus Status { get; set; }

    public TranslationRoomType TranslationRoomType { get; set; }

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

