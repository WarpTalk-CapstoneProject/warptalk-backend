using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// G11 — subscription.addons: a recurring extra on top of a plan (migration 20260925160000).
/// Each unit raises one real entitlement by <see cref="UnitsPerQuantity"/> — see
/// <see cref="PackageCatalogConstants.AddonEntitlements"/> for the keys that can be sold, and
/// EntitlementResolver for where the grant is applied.
/// </summary>
public class Addon : IStripeSyncedCatalogItem
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>What one unit is called to a customer: "participant", "language", "room".</summary>
    public string UnitLabel { get; set; } = string.Empty;

    /// <summary>One of <see cref="PackageCatalogConstants.AddonEntitlements.All"/>.</summary>
    public string EntitlementKey { get; set; } = string.Empty;

    /// <summary>How much one unit adds to a numeric entitlement. 1 for a capability switch.</summary>
    public int UnitsPerQuantity { get; set; } = 1;

    public decimal? PriceMonthlyVnd { get; set; }
    public decimal? PriceYearlyVnd { get; set; }
    public decimal? PriceMonthlyUsd { get; set; }
    public decimal? PriceYearlyUsd { get; set; }

    public int MinQuantity { get; set; } = 1;
    public int MaxQuantity { get; set; } = 1;

    /// <summary>Plans whose workspaces may buy this add-on. Empty = every plan.</summary>
    public Guid[] EligiblePlanIds { get; set; } = [];

    public string Status { get; set; } = PackageCatalogConstants.Statuses.Draft;
    public int SortOrder { get; set; }

    public string? StripeProductId { get; set; }
    public string StripePriceIds { get; set; } = "{}";
    public DateTime? StripeSyncedAt { get; set; }
    public string? StripeSyncError { get; set; }
    public string? StripeSyncedHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public decimal? PriceFor(string billingCycle, string currency)
    {
        var yearly = string.Equals(billingCycle, SubscriptionConstants.BillingCycles.Yearly, StringComparison.OrdinalIgnoreCase);
        return PackageCatalogConstants.Currencies.Normalize(currency) switch
        {
            PackageCatalogConstants.Currencies.Vnd => yearly ? PriceYearlyVnd : PriceMonthlyVnd,
            PackageCatalogConstants.Currencies.Usd => yearly ? PriceYearlyUsd : PriceMonthlyUsd,
            _ => null,
        };
    }
}
