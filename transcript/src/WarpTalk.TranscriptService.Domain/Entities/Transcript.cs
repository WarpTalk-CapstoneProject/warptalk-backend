using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class Transcript
{
    public Guid Id { get; set; }

    /// <summary>
    /// External AuthService workspace id. No physical FK.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// External TranslationRoomService room id. No physical FK.
    /// </summary>
    public Guid TranslationRoomId { get; set; }

    public int Version { get; set; }

    public string Status { get; set; } = null!;

    public string SourceLanguage { get; set; } = null!;

    public int TotalSegments { get; set; }

    public int TotalDurationMs { get; set; }

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

    public DateTime? FinalizedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? DeletedBy { get; set; }

    /// <summary>Head-pointer: at most one transcript per translation_room_id has this true (transcripts_one_current_per_room_idx).</summary>
    public bool IsCurrent { get; set; } = true;

    public Guid? PreviousTranscriptId { get; set; }

    public string? EngineVersion { get; set; }

    /// <summary>Advance with `UPDATE ... SET last_sequence_order = last_sequence_order + 1 RETURNING` in the same transaction as the segment insert — never read-MAX-then-insert.</summary>
    public int LastSequenceOrder { get; set; }

    public virtual ICollection<TranscriptExport> TranscriptExports { get; set; } = new List<TranscriptExport>();

    public virtual ICollection<TranscriptSegment> TranscriptSegments { get; set; } = new List<TranscriptSegment>();
}
