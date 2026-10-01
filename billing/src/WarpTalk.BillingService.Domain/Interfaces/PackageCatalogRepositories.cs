using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

// G11 — one repository per catalog entity, as the rest of billing does (no Repository<T>() off
// the unit of work). Each adds only the reads a generic surface cannot express: the sales
// aggregates the admin list shows, and the per-workspace counts the purchase limits check.

/// <summary>Money summed per currency, lower-case currency code → amount.</summary>
public sealed record CurrencyTotal(string Currency, decimal Amount);

/// <summary>What one sellable item has sold, from the purchase/redemption rows.</summary>
public sealed record CatalogItemSales(
    Guid ItemId,
    int UnitsSold,
    int ActiveSubscribers,
    int ActiveQuantity,
    IReadOnlyList<CurrencyTotal> Revenue);

public interface ICreditPackRepository : IGenericRepository<CreditPack>
{
    Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct = default);
}

public interface IAddonRepository : IGenericRepository<Addon>
{
    Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct = default);
}

public interface ICouponRepository : IGenericRepository<Coupon>
{
    Task<bool> CodeExistsAsync(string code, Guid? exceptId, CancellationToken ct = default);

    Task<Coupon?> GetByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Active auto-apply campaigns. The caller filters by window and item.</summary>
    Task<IReadOnlyList<Coupon>> GetActiveAutoApplyAsync(CancellationToken ct = default);
}

public interface ICreditPackPurchaseRepository : IGenericRepository<CreditPackPurchase>
{
    Task<int> CountForWorkspaceAsync(Guid creditPackId, Guid workspaceId, CancellationToken ct = default);

    Task<int> CountForPackAsync(Guid creditPackId, CancellationToken ct = default);

    Task<bool> ExistsForSessionAsync(string stripeSessionId, CancellationToken ct = default);

    /// <summary>Units sold and revenue per pack, over every purchase ever recorded.</summary>
    Task<IReadOnlyList<CatalogItemSales>> GetSalesAsync(CancellationToken ct = default);

    /// <summary>Purchases whose expiry has passed and that have not been swept yet.</summary>
    Task<IReadOnlyList<CreditPackPurchase>> GetDueForExpiryAsync(DateTime nowUtc, int take, CancellationToken ct = default);
}

public interface IWorkspaceAddonRepository : IGenericRepository<WorkspaceAddon>
{
    /// <summary>Add-ons still granting (active, or cancelling within the paid period), with their catalog row.</summary>
    Task<IReadOnlyList<WorkspaceAddon>> GetGrantingForWorkspaceAsync(Guid workspaceId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Every add-on row of a workspace that is not cancelled, with its catalog row.</summary>
    Task<IReadOnlyList<WorkspaceAddon>> GetOpenForWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);

    Task<WorkspaceAddon?> GetByStripeSubscriptionIdAsync(string stripeSubscriptionId, CancellationToken ct = default);

    Task<bool> ExistsForSessionAsync(string stripeSessionId, CancellationToken ct = default);

    /// <summary>Active subscribers, live quantity and billed revenue per add-on.</summary>
    Task<IReadOnlyList<CatalogItemSales>> GetSalesAsync(CancellationToken ct = default);
}

public interface ICouponRedemptionRepository : IGenericRepository<CouponRedemption>
{
    Task<int> CountForCouponAsync(Guid couponId, CancellationToken ct = default);

    Task<int> CountForWorkspaceAsync(Guid couponId, Guid workspaceId, CancellationToken ct = default);

    Task<bool> ExistsForSessionAsync(string stripeSessionId, CancellationToken ct = default);

    /// <summary>Redemptions and total discount given per coupon (Revenue carries the discount).</summary>
    Task<IReadOnlyList<CatalogItemSales>> GetSalesAsync(CancellationToken ct = default);
}
