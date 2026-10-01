using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// G11 — subscription.coupon_redemptions: one paid checkout that used a coupon. Written by the
/// payment path in the same transaction as the payment row, once per Stripe session (unique), so
/// the redemption limits count money that actually moved.
/// </summary>
public class CouponRedemption
{
    public Guid Id { get; set; }
    public Guid CouponId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public Guid? ItemId { get; set; }
    public string StripeSessionId { get; set; } = string.Empty;
    public Guid? PaymentId { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal DiscountAmount { get; set; }
    public DateTime RedeemedAt { get; set; }
}
