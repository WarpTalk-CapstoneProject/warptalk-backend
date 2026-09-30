using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// G11 — the Stripe side of a sellable item, shared by <see cref="CreditPack"/> and
/// <see cref="Addon"/>. Stripe prices are immutable: a price change creates a new Price and
/// archives the old one, so the ids here always name the price a checkout should use NOW.
/// </summary>
public interface IStripeSyncedCatalogItem
{
    Guid Id { get; }
    string? StripeProductId { get; set; }

    /// <summary>jsonb map of price key (<c>vnd</c>, <c>monthly_usd</c>, …) → Stripe price id.</summary>
    string StripePriceIds { get; set; }

    DateTime? StripeSyncedAt { get; set; }
    string? StripeSyncError { get; set; }

    /// <summary>
    /// Fingerprint of the sellable terms at the last successful sync. When the current terms hash
    /// differently the item is shown as "outdated": edited here, not yet pushed to Stripe.
    /// </summary>
    string? StripeSyncedHash { get; set; }
}
