using System;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// Biên bản họp — the meeting record a person signs.
///
/// Lifecycle:
///   DRAFT -&gt; IN_REVIEW -&gt; APPROVED
///
/// An APPROVED row is never edited. Correcting it means a new row at <c>Version + 1</c> pointing
/// back through <see cref="PreviousMinutesId"/>, with <see cref="IsCurrent"/> moving to the new
/// one — so what was actually signed stays readable afterwards.
///
/// Distinct from a SUMMARY_EXPORT artifact on purpose. The summary is what a model wrote and
/// nobody owns; this has a number, a date, an attendance list, a named secretary and a chair.
/// </summary>
public partial class MeetingMinutes
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    /// <summary>
    /// External AuthService workspace id. No physical FK.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>Human-facing identity, unique within a workspace: <c>BB-{year}-{sequence}</c>.</summary>
    public string MinutesNo { get; set; } = null!;

    public string Status { get; set; } = null!;

    public int Version { get; set; }

    /// <summary>Head pointer: at most one row per room has this true (meeting_minutes_one_current_per_room_idx).</summary>
    public bool IsCurrent { get; set; } = true;

    public Guid? PreviousMinutesId { get; set; }

    /// <summary>
    /// The transcript version this draft was drawn from. A later re-transcription does NOT rewrite
    /// signed minutes — it makes them show as needing a revision, which is how an organisation
    /// actually handles a record that has already been approved.
    /// </summary>
    public int? BasedOnTranscriptVersion { get; set; }

    /// <summary>
    /// The program that produced the draft. Never the answerable party: that is
    /// <see cref="SecretaryParticipantId"/>, and nothing but a human signature fills it.
    /// </summary>
    public string? DraftedByEngine { get; set; }

    public DateTime? DraftedAt { get; set; }

    /// <summary>translation_room_participants.id of the secretary who signed. No physical FK.</summary>
    public Guid? SecretaryParticipantId { get; set; }

    public DateTime? SecretarySignedAt { get; set; }

    /// <summary>translation_room_participants.id of the chair who approved. No physical FK.</summary>
    public Guid? ChairParticipantId { get; set; }

    public DateTime? ChairApprovedAt { get; set; }

    /// <summary>
    /// How many items the secretary changed before signing. This is shown to the reader: it is the
    /// only evidence they have that a person read the draft rather than approving it unseen.
    /// </summary>
    public int EditCountVsDraft { get; set; }

    /// <summary>The structured document. See MeetingMinutesContent for the shape.</summary>
    public string Content { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }

    /// <summary>External AuthService user id. No physical FK.</summary>
    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>External AuthService user id. No physical FK.</summary>
    public Guid? UpdatedBy { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;
}
