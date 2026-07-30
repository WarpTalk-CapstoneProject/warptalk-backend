using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class CreditTransaction
{
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    /// <summary>External AuthService user id attributed to this charge/credit. No physical FK.</summary>
    public Guid UserId { get; set; }

    /// <summary>Kept for GetTransactionHistoryAsync/GetCreditHistoryAsync queries scoped by workspace (not a DB column — populated via the Subscription navigation).</summary>
    public Guid WorkspaceId { get; set; }

    public int Amount { get; set; }

    public string Type { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? ReferenceId { get; set; }

    /// <summary>
    /// Free-form string (no CHECK constraint on subscription.credit_transactions.reference_type,
    /// unlike the old billing.credit_transactions design) — matches billing_worker's own
    /// reference_type values (e.g. "transcript_segment", "translation_content", "audio_dubbing").
    /// </summary>
    public string? ReferenceType { get; set; }

    public int BalanceAfter { get; set; }

    public string? ChargeType { get; set; }

    public Guid? PricingRateCardId { get; set; }

    public Guid? UsageRecordId { get; set; }

    public decimal? UnitPriceSnapshot { get; set; }

    public Guid? InvoiceId { get; set; }

    public Guid? ReversalOfTransactionId { get; set; }

    public string? Currency { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? CorrelationId { get; set; }

    public string Status { get; set; } = "committed";

    public Guid? TriggeredByParticipantId { get; set; }

    /// <summary>
    /// External TranscriptService transcript_segments.id, cross-service, no physical FK.
    /// Populated by billing_worker (Python) for every charge_type — unlike ReferenceId/
    /// ReferenceType (which store "translation_content"/"audio_dubbing" for non-STT charges),
    /// this stays populated with the real segment id regardless of charge_type.
    /// </summary>
    public Guid? TranscriptSegmentId { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Subscription? Subscription { get; set; }
}
