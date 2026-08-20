using System;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// One commitment from an approved biên bản, as a row somebody can be assigned and can close.
///
/// Created only when minutes are APPROVED. Before that the document is a draft, and a draft's
/// commitments are proposals — putting them in people's task lists would mean withdrawing them
/// whenever the secretary edited a line.
/// </summary>
public partial class MeetingActionItem
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    /// <summary>External AuthService workspace id. No physical FK.</summary>
    public Guid WorkspaceId { get; set; }

    public Guid SourceMinutesId { get; set; }

    /// <summary>
    /// Denormalised from the room. Carry-over asks what the previous occurrence of a recurring
    /// booking left open, and that question should not have to join to find its own predecessor.
    /// </summary>
    public Guid? SeriesId { get; set; }

    public string Task { get; set; } = null!;

    /// <summary>What the meeting said. Part of the record; never overwritten by resolution.</summary>
    public string? OwnerName { get; set; }

    /// <summary>
    /// translation_room_participants.id, when the name matched exactly one person. NULL means the
    /// name was ambiguous or matched nobody — never "no owner was named", which is OwnerName being
    /// empty.
    /// </summary>
    public Guid? OwnerParticipantId { get; set; }

    /// <summary>
    /// External AuthService user id, copied at resolution time so a purged participant row does
    /// not orphan somebody's task list. No physical FK.
    /// </summary>
    public Guid? AssigneeUserId { get; set; }

    /// <summary>
    /// Where in the meeting the commitment was made. The same citation the summary carries — and
    /// the key a revision uses to recognise a task it has already created.
    /// </summary>
    public long? AtMs { get; set; }

    public string Status { get; set; } = null!;

    public DateOnly? DueDate { get; set; }

    /// <summary>Set when this task continues one an earlier meeting left open.</summary>
    public Guid? CarriedFromActionItemId { get; set; }

    public DateTime? ClosedAt { get; set; }

    /// <summary>External AuthService user id. No physical FK.</summary>
    public Guid? ClosedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
