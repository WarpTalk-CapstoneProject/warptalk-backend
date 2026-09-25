using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// G11 — subscription.credit_packs: a one-off bundle of credits a workspace can buy on top of its
/// plan (migration 20260925160000). Buying one grants <see cref="Credits"/> +
/// <see cref="BonusCredits"/> to the workspace's subscription balance through the same ledger the
/// custom top-up writes.
/// </summary>
public class CreditPack : IStripeSyncedCatalogItem
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public int Credits { get; set; }
    public int BonusCredits { get; set; }

    /// <summary>Whole dong (VND is zero-decimal). Null = not sold in VND.</summary>
    public decimal? PriceVnd { get; set; }

    /// <summary>US dollars, two decimals. Null = not sold in USD.</summary>
    public decimal? PriceUsd { get; set; }

    /// <summary>Days after purchase that the pack's unspent credits expire. Null = never.</summary>
    public int? ValidityDays { get; set; }

    /// <summary>One of <see cref="PackageCatalogConstants.Visibility"/>.</summary>
    public string Visibility { get; set; } = PackageCatalogConstants.Visibility.Public;

    public Guid[] EligiblePlanIds { get; set; } = [];
    public Guid[] EligibleWorkspaceIds { get; set; } = [];

    /// <summary>Lifetime purchases allowed per workspace. Null = unlimited.</summary>
    public int? MaxPerWorkspace { get; set; }

    /// <summary>Lifetime purchases allowed across all workspaces. Null = unlimited.</summary>
    public int? MaxTotal { get; set; }

    public DateTime? AvailableFrom { get; set; }
    public DateTime? AvailableUntil { get; set; }

    /// <summary>One of <see cref="PackageCatalogConstants.Statuses"/>.</summary>
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

    public decimal? PriceFor(string currency) =>
        PackageCatalogConstants.Currencies.Normalize(currency) switch
        {
            PackageCatalogConstants.Currencies.Vnd => PriceVnd,
            PackageCatalogConstants.Currencies.Usd => PriceUsd,
            _ => null,
        };

    public int TotalCredits => Credits + BonusCredits;
}
