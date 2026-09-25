using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// G11 — pushes a catalog row to Stripe and reads it back. Implemented in Infrastructure over the
/// Stripe SDK. The methods MUTATE the entity's Stripe fields (ids, synced-at, error, fingerprint)
/// and never save; the caller commits, so the audit interceptor records the change.
///
/// Stripe objects are never deleted: a changed price creates a new Price and deactivates the old
/// one; an archived item's Product and Prices are deactivated; a coupon whose terms changed gets a
/// new Stripe coupon and the old promotion code is deactivated.
/// </summary>
public interface IStripeCatalogSync
{
    bool IsConfigured { get; }

    /// <summary>Syncs the pack. On failure sets <c>StripeSyncError</c> and returns the reason.</summary>
    Task<string?> SyncCreditPackAsync(CreditPack pack, CancellationToken ct = default);

    Task<string?> SyncAddonAsync(Addon addon, CancellationToken ct = default);

    /// <param name="productIdsByItem">Stripe product ids of the packs/add-ons the coupon is limited to.</param>
    Task<string?> SyncCouponAsync(Coupon coupon, IReadOnlyCollection<string> productIdsByItem, CancellationToken ct = default);

    Task<IReadOnlyList<StripeDriftItemDto>> CompareCreditPackAsync(CreditPack pack, CancellationToken ct = default);

    Task<IReadOnlyList<StripeDriftItemDto>> CompareAddonAsync(Addon addon, CancellationToken ct = default);

    Task<IReadOnlyList<StripeDriftItemDto>> CompareCouponAsync(Coupon coupon, CancellationToken ct = default);
}
