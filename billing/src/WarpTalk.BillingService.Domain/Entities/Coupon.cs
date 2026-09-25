using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// G11 — subscription.coupons: a discount on plans, credit packs or add-ons (migration
/// 20260925160000). A coupon with a <see cref="Code"/> is typed at checkout; one with
/// <see cref="AutoApply"/> is a campaign applied to every eligible checkout without a code.
/// </summary>
public class Coupon
{
    public Guid Id { get; set; }

    /// <summary>Upper-case redemption code. Null for an auto-apply campaign.</summary>
    public string? Code { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>One of <see cref="PackageCatalogConstants.DiscountTypes"/>.</summary>
    public string DiscountType { get; set; } = PackageCatalogConstants.DiscountTypes.Percent;

    /// <summary>0 &lt; percent ≤ 100 when <see cref="DiscountType"/> is percent.</summary>
    public decimal? PercentOff { get; set; }

    /// <summary>Money off, in <see cref="AmountOffCurrency"/>, when the type is fixed.</summary>
    public decimal? AmountOff { get; set; }

    public string? AmountOffCurrency { get; set; }

    /// <summary>Subset of <see cref="PackageCatalogConstants.ItemTypes"/>.</summary>
    public string[] AppliesToTypes { get; set; } = [];

    /// <summary>Specific plans / packs / add-ons. Empty = every item of the listed types.</summary>
    public Guid[] AppliesToIds { get; set; } = [];

    /// <summary>One of <see cref="PackageCatalogConstants.Durations"/>.</summary>
    public string Duration { get; set; } = PackageCatalogConstants.Durations.Once;

    public int? DurationInMonths { get; set; }

    /// <summary>Total redemptions allowed. Null = unlimited.</summary>
    public int? MaxRedemptions { get; set; }

    /// <summary>Redemptions allowed per workspace.</summary>
    public int PerWorkspaceLimit { get; set; } = 1;

    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }

    public bool AutoApply { get; set; }

    public string Status { get; set; } = PackageCatalogConstants.Statuses.Draft;

    public string? StripeCouponId { get; set; }
    public string? StripePromotionCodeId { get; set; }
    public DateTime? StripeSyncedAt { get; set; }
    public string? StripeSyncError { get; set; }
    public string? StripeSyncedHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? ArchivedAt { get; set; }
}
