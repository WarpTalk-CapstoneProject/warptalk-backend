using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class TranscriptSegment
{
    public Guid Id { get; set; }

    public Guid TranscriptId { get; set; }

    /// <summary>
    /// External TranslationRoomService participant id. No physical FK.
    /// </summary>
    public Guid? SpeakerParticipantId { get; set; }

    public string SpeakerName { get; set; } = null!;

    public string OriginalText { get; set; } = null!;

    public string OriginalLanguage { get; set; } = null!;

    public int StartTimeMs { get; set; }

    public int EndTimeMs { get; set; }

    /// <summary>
    /// The STT model's own confidence for this segment (an avg_logprob, so ≤ 0), or NULL when the
    /// producer reported none. WT-277: NULL genuinely means "unknown" — it must never be coalesced
    /// to a number on write, because a fabricated 1.0000 is indistinguishable from a perfect score.
    /// </summary>
    public decimal? Confidence { get; set; }

    public int SequenceOrder { get; set; }

    public bool IsCorrected { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Billing (STT/TRANSLATION/AUDIO_DUBBING charges) must only fire once this is true — never on interim STT drafts.</summary>
    public bool IsFinal { get; set; } = true;

    /// <summary>Cross-version match — the equivalent segment in a previous transcript version (re-recording/re-STT), self-referencing FK.</summary>
    public Guid? MatchedSegmentId { get; set; }

    public virtual Transcript Transcript { get; set; } = null!;

    public virtual ICollection<TranscriptCorrection> TranscriptCorrections { get; set; } = new List<TranscriptCorrection>();

    public virtual ICollection<SegmentTranslationLink> SegmentTranslationLinks { get; set; } = new List<SegmentTranslationLink>();
}
