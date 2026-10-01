using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// G11 — subscription.credit_pack_purchases: one paid credit pack. Written with the ledger row
/// that grants the credits, keyed by the Stripe session so a webhook and the return page cannot
/// grant the same pack twice. The row is also where expiry is tracked.
/// </summary>
public class CreditPackPurchase
{
    public Guid Id { get; set; }
    public Guid CreditPackId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid? PaymentId { get; set; }
    public string StripeSessionId { get; set; } = string.Empty;
    public int Credits { get; set; }
    public int BonusCredits { get; set; }
    public string Currency { get; set; } = string.Empty;

    /// <summary>What Stripe charged, after any coupon.</summary>
    public decimal AmountPaid { get; set; }

    public Guid? CouponId { get; set; }
    public decimal DiscountAmount { get; set; }
    public DateTime PurchasedAt { get; set; }

    /// <summary>When the unspent part of this pack expires. Null = never.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Credits removed at expiry (0 until the expiry sweep has run).</summary>
    public int ExpiredCredits { get; set; }

    public DateTime? ExpiredAt { get; set; }

    public int TotalCredits => Credits + BonusCredits;
}
