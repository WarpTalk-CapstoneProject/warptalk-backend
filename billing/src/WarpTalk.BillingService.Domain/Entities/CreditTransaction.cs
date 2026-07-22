using System;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class CreditTransaction
{
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public int Amount { get; set; }

    public string Type { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? ReferenceId { get; set; }

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

    public Guid? TriggeredByParticipantId { get; set; }

    public Guid? TranscriptSegmentId { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Subscription? Subscription { get; set; }
}
