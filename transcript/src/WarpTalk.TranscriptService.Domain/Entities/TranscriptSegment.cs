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

    /// <summary>
    /// WT-716 tier 1: <see cref="OriginalText"/> with fillers and stutters removed by stt_worker.
    /// Null means NOT CLEANED (older rows, older producer) — read <see cref="OriginalText"/>. An
    /// empty string is different: the segment was filler only, and a clean view hides it.
    /// <see cref="OriginalText"/> itself is never rewritten by cleaning.
    /// </summary>
    public string? CleanText { get; set; }

    /// <summary>WT-716: subset of filler_only, fillers_removed, stutter_removed, escalate. Null exactly when <see cref="CleanText"/> is.</summary>
    public string[]? CleanFlags { get; set; }

    /// <summary>
    /// EXTERNAL_BRIDGE only: which person on the Google Meet side said this stand-in segment — the
    /// Meet participant resource name (<c>conferenceRecords/{id}/participants/{id}</c>) when
    /// Google's transcript attributed it, or a host-chosen key. Null means "the Meet side, nobody
    /// in particular" and <see cref="SpeakerName"/> then reads "Google Meet participants".
    /// <see cref="SpeakerName"/> always carries the display name; this is the identity behind it.
    /// For a live source the key IS stt_worker's guessed name, and <see cref="SpeakerName"/> shows it
    /// only at or above the configured confidence (WarpTalk.Shared.FarSpeakerNames) — the hint is
    /// stored either way.
    /// </summary>
    public string? FarSpeakerKey { get; set; }

    /// <summary>Where <see cref="FarSpeakerKey"/> came from: <see cref="Domain.FarSpeakerSources"/>, or the live source stt_worker reported on stt:results.</summary>
    public string? FarSpeakerSource { get; set; }

    /// <summary>0..1. For <c>google_transcript</c>, the fraction of this segment's time the chosen Meet transcript entry covers; for a live source, the producer's own score.</summary>
    public float? FarSpeakerConfidence { get; set; }

    public virtual Transcript Transcript { get; set; } = null!;

    public virtual ICollection<TranscriptCorrection> TranscriptCorrections { get; set; } = new List<TranscriptCorrection>();

    public virtual ICollection<SegmentTranslationLink> SegmentTranslationLinks { get; set; } = new List<SegmentTranslationLink>();
}
