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
/// IN_PROGRESS -> FAILED
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
    /// Who BOOKED this room. External AuthService user id. No physical FK.
    ///
    /// Stamped once at creation and never moved — a host handover is recorded on
    /// <see cref="ActiveHostId"/>, not here. This column is what the meeting list filters by,
    /// what a recurring series belongs to, and what usage is attributed to, so moving it would
    /// hand all of that to whoever happened to be handed the microphone.
    /// </summary>
    public Guid HostId { get; set; }

    /// <summary>
    /// WT-359: who is RUNNING this room now, after a Transfer Host. Null means nobody took it
    /// over and <see cref="HostId"/> is still running it — true of every room created before this
    /// column, which is why it needed no backfill.
    ///
    /// Always ask <see cref="EffectiveHostId"/> or <see cref="IsHostedBy"/> rather than reading
    /// this directly; a bare <c>HostId == userId</c> is the bug this column exists to fix.
    /// </summary>
    public Guid? ActiveHostId { get; set; }

    /// <summary>
    /// The user who holds host authority right now: the transferee if there has been a handover,
    /// otherwise the booker.
    /// </summary>
    public Guid EffectiveHostId => ActiveHostId ?? HostId;

    /// <summary>
    /// Whether <paramref name="userId"/> holds host authority over this room right now. Every
    /// host-gated operation asks this — start, pause, resume, stop, end, cancel, settings and the
    /// join-time role assignment — so that a transfer moves all of them together or none of them.
    /// </summary>
    public bool IsHostedBy(Guid userId) => EffectiveHostId == userId;

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public string TranslationRoomCode { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string TranslationRoomType { get; set; } = null!;

    public string? ExternalProvider { get; set; }

    public string? ExternalMeetingUrl { get; set; }

    public string? ExternalCalendarEventId { get; set; }

    public string? ExternalCalendarEventUrl { get; set; }

    public int MaxParticipants { get; set; }

    public string SourceLanguage { get; set; } = null!;

    public string TargetLanguages { get; set; } = null!;

    public string Settings { get; set; } = null!;

    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// WT-327: the recurring series this room is an occurrence of, or null for a one-off room —
    /// which is every room that existed before this column.
    ///
    /// A series occurrence is an ORDINARY room in every other respect: same statuses, same
    /// lifecycle, its own code, its own transcript, its own billing. This column is a
    /// back-reference so the series can be cancelled as a unit and so the UI can say "this
    /// repeats"; nothing downstream is required to read it.
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// WT-327: the local calendar date (in the series' own time zone) this occurrence was
    /// generated for. Unique per series — that uniqueness IS the idempotency of the
    /// materialisation sweep, so a double-run, a restart mid-pass or two service replicas
    /// cannot produce two rooms for the same day.
    /// </summary>
    public DateOnly? SeriesOccurrenceLocalDate { get; set; }
    /// <summary>WT-326: set once the T-30min reminder notification has been sent for this room.</summary>
    public DateTime? Reminder30MinSentAt { get; set; }

    /// <summary>WT-14: set once the T-10min reminder notification has been sent for this room.</summary>
    public DateTime? Reminder10MinSentAt { get; set; }

    /// <summary>WT-14: set once the T-1min reminder notification has been sent for this room.</summary>
    public DateTime? Reminder1MinSentAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public int? DurationSeconds { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? DeletedBy { get; set; }

    /// <summary>WT-327: null unless <see cref="SeriesId"/> is set.</summary>
    public virtual TranslationRoomSeries? Series { get; set; }

    public virtual ICollection<TranslationRoomArtifact> TranslationRoomArtifacts { get; set; } = new List<TranslationRoomArtifact>();

    public virtual ICollection<TranslationRoomAudioRoute> TranslationRoomAudioRoutes { get; set; } = new List<TranslationRoomAudioRoute>();

    public virtual ICollection<TranslationRoomFeedback> TranslationRoomFeedbacks { get; set; } = new List<TranslationRoomFeedback>();

    public virtual ICollection<TranslationRoomParticipant> TranslationRoomParticipants { get; set; } = new List<TranslationRoomParticipant>();

    public virtual ICollection<TranslationRoomInvitation> TranslationRoomInvitations { get; set; } = new List<TranslationRoomInvitation>();

    public virtual ICollection<TranslationRoomSession> TranslationRoomSessions { get; set; } = new List<TranslationRoomSession>();
}
