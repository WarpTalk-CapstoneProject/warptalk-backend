using System;

namespace WarpTalk.TranscriptService.Domain.Entities;

/// <summary>
/// Join row linking a transcript_segment to its (deduplicated) translation for one target
/// language. Composite primary key (SegmentId, TranslationContentId) — no surrogate Id.
/// At most one row per (segment_id, target_language) has IsCurrent = true
/// (segment_translation_links_current_unique_idx); re-translation (e.g. after a
/// correction) inserts a new link and flips the old one's IsCurrent to false rather than
/// mutating translated text in place.
/// </summary>
public partial class SegmentTranslationLink
{
    public Guid SegmentId { get; set; }

    public Guid TranslationContentId { get; set; }

    /// <summary>Denormalized from TranslationContent.TargetLanguage at insert time — lets the current-per-language unique index be enforced without a join.</summary>
    public string TargetLanguage { get; set; } = null!;

    public bool IsCurrent { get; set; } = true;

    public DateTime? DeliveredAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual TranscriptSegment Segment { get; set; } = null!;

    public virtual TranslationContent TranslationContent { get; set; } = null!;
}
