using System;

namespace WarpTalk.TranscriptService.Domain.Entities;

/// <summary>
/// WT-716 tier 2. One whole, cleaned sentence — fillers and stutters gone, fragments merged — laid
/// over the raw <see cref="TranscriptSegment"/> rows it was made from.
///
/// A VIEW over segments, never a replacement for them. Summary citations and transcript
/// corrections anchor raw segment ids, so the raw rows stay exactly as recognised and this row
/// only says "read segments X, Y, Z as this sentence". That is why <see cref="SegmentIds"/> is the
/// load-bearing column and why there is no foreign key on it: the LLM tier can finish a sentence
/// before the consumer has persisted every segment it covers, and refusing the sentence for that
/// would lose it rather than delay it.
///
/// <see cref="Id"/> is the producer's sentence_id. The producer revises a sentence as more of the
/// conversation arrives; a higher <see cref="Revision"/> replaces the row, a lower or equal one is
/// a redelivery or an out-of-order write and is ignored.
/// </summary>
public partial class TranscriptCleanSentence
{
    public Guid Id { get; set; }

    public Guid TranscriptId { get; set; }

    /// <summary>External TranslationRoomService participant id. No physical FK. Null for "system".</summary>
    public Guid? SpeakerParticipantId { get; set; }

    /// <summary>Raw segment ids, in conversation order. May name segments not persisted yet.</summary>
    public Guid[] SegmentIds { get; set; } = Array.Empty<Guid>();

    public string CleanText { get; set; } = null!;

    public string Language { get; set; } = null!;

    /// <summary>Subset of self_repair, fallback_raw, escalate.</summary>
    public string[] Flags { get; set; } = Array.Empty<string>();

    /// <summary>llm | prepass.</summary>
    public string Source { get; set; } = null!;

    /// <summary>
    /// Also the EF concurrency token: two consumers applying different revisions of one sentence
    /// at once must not let the lower one win because it saved last.
    /// </summary>
    public int Revision { get; set; }

    /// <summary>Earliest start of the covered segments already stored when this revision was written.</summary>
    public int? StartTimeMs { get; set; }

    /// <summary>The producer's timestamp_ms for this revision.</summary>
    public DateTime? ProducedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Transcript Transcript { get; set; } = null!;
}
