using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class TranscriptCorrection
{
    public Guid Id { get; set; }

    public Guid SegmentId { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid UserId { get; set; }

    public string OriginalText { get; set; } = null!;

    public string CorrectedText { get; set; } = null!;

    public string CorrectionType { get; set; } = null!;

    public string Status { get; set; } = null!;

    public bool TriggeredRetranslation { get; set; }

    /// <summary>
    /// External AuthService user id. No physical FK.
    /// </summary>
    public Guid? ReviewedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Only set when CorrectionType == "TRANSLATION" — points at the translation_contents row this correction re-does.</summary>
    public Guid? TranslationContentId { get; set; }

    /// <summary>Soft ref -> subscription.credit_transactions (cross-service, no physical FK) — the reversal transaction issued when this correction invalidates a prior charge.</summary>
    public Guid? ReversalCreditTransactionId { get; set; }

    public virtual TranscriptSegment Segment { get; set; } = null!;

    public virtual TranslationContent? TranslationContent { get; set; }
}
