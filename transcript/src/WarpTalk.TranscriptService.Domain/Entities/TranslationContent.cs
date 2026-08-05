using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Domain.Entities;

/// <summary>
/// Deduplicated translation text, keyed by (workspace_id, text_hash, target_language) via
/// translation_contents_dedup_idx. Replaces the 1:1 TranscriptTranslation design — a segment
/// links to one of these through SegmentTranslationLink instead of owning its own copy of
/// the translated text, so two participants hearing the same sentence share one row.
/// </summary>
public partial class TranslationContent
{
    public Guid Id { get; set; }

    /// <summary>External AuthService workspace id. No physical FK.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>MD5 of TranslatedText — the dedup key alongside workspace_id/target_language (matches migration 017's own backfill: md5(translated_text)).</summary>
    public string TextHash { get; set; } = null!;

    public string TargetLanguage { get; set; } = null!;

    public string TranslatedText { get; set; } = null!;

    public string TranslatorModel { get; set; } = null!;

    /// <summary>
    /// The STT confidence (avg_logprob) of the source segment this translation came from — NOT a
    /// translation quality signal. WT-278: warptalk-ai's translator produces no score of its own,
    /// so translation_worker copies the upstream STTResultMessage's value; under its old name
    /// ("confidence") it read as if it measured the translation. Do not surface it as translation
    /// quality anywhere. NULL means the source segment carried no usable confidence (WT-277).
    /// </summary>
    public decimal? SourceSttConfidence { get; set; }

    public bool IsRetranslated { get; set; }

    public Guid? PreviousTranslationContentId { get; set; }

    public int? LatencyMs { get; set; }

    /// <summary>Claim-state: pending | processing | done | failed.</summary>
    public string Status { get; set; } = "pending";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual TranslationContent? PreviousTranslationContent { get; set; }

    public virtual ICollection<SegmentTranslationLink> SegmentTranslationLinks { get; set; } = new List<SegmentTranslationLink>();

    public virtual ICollection<AudioDubbing> AudioDubbings { get; set; } = new List<AudioDubbing>();
}
