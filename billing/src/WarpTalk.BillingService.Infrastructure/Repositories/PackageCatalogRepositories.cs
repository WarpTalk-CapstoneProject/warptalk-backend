using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

// G11 — repositories of the sellable catalog (credit packs, add-ons, coupons) and of what has
// been bought from it. The catalog tables hold tens of rows; the purchase tables grow with sales,
// so every aggregate here is a GROUP BY in the database, never a load-then-sum.

public class CreditPackRepository : GenericRepository<CreditPack>, ICreditPackRepository
{
    public CreditPackRepository(BillingDbContext db) : base(db) { }

    public Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct = default)
        => _dbSet.AnyAsync(p => p.Slug == slug && (exceptId == null || p.Id != exceptId), ct);
}

public class AddonRepository : GenericRepository<Addon>, IAddonRepository
{
    public AddonRepository(BillingDbContext db) : base(db) { }

    public Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct = default)
        => _dbSet.AnyAsync(a => a.Slug == slug && (exceptId == null || a.Id != exceptId), ct);
}

public class CouponRepository : GenericRepository<Coupon>, ICouponRepository
{
    public CouponRepository(BillingDbContext db) : base(db) { }

    public Task<bool> CodeExistsAsync(string code, Guid? exceptId, CancellationToken ct = default)
        => _dbSet.AnyAsync(c => c.Code == code && (exceptId == null || c.Id != exceptId), ct);

    public Task<Coupon?> GetByCodeAsync(string code, CancellationToken ct = default)
        => _dbSet.FirstOrDefaultAsync(c => c.Code == code, ct);

    public async Task<IReadOnlyList<Coupon>> GetActiveAutoApplyAsync(CancellationToken ct = default)
        => await _dbSet.AsNoTracking()
            .Where(c => c.AutoApply && c.Status == PackageCatalogConstants.Statuses.Active)
            .ToListAsync(ct);
}

public class CreditPackPurchaseRepository : GenericRepository<CreditPackPurchase>, ICreditPackPurchaseRepository
{
    public CreditPackPurchaseRepository(BillingDbContext db) : base(db) { }

    public Task<int> CountForWorkspaceAsync(Guid creditPackId, Guid workspaceId, CancellationToken ct = default)
        => _dbSet.CountAsync(p => p.CreditPackId == creditPackId && p.WorkspaceId == workspaceId, ct);

    public Task<int> CountForPackAsync(Guid creditPackId, CancellationToken ct = default)
        => _dbSet.CountAsync(p => p.CreditPackId == creditPackId, ct);

    public Task<bool> ExistsForSessionAsync(string stripeSessionId, CancellationToken ct = default)
        => _dbSet.AnyAsync(p => p.StripeSessionId == stripeSessionId, ct);

    public async Task<IReadOnlyList<CatalogItemSales>> GetSalesAsync(CancellationToken ct = default)
    {
        var rows = await _dbSet.AsNoTracking()
            .GroupBy(p => new { p.CreditPackId, p.Currency })
            .Select(g => new { g.Key.CreditPackId, g.Key.Currency, Units = g.Count(), Revenue = g.Sum(p => p.AmountPaid) })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.CreditPackId)
            .Select(g => new CatalogItemSales(
                g.Key,
                g.Sum(r => r.Units),
                0,
                0,
                g.Select(r => new CurrencyTotal(r.Currency, r.Revenue)).OrderBy(t => t.Currency).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<CreditPackPurchase>> GetDueForExpiryAsync(DateTime nowUtc, int take, CancellationToken ct = default)
        => await _dbSet
            .Where(p => p.ExpiresAt != null && p.ExpiresAt <= nowUtc && p.ExpiredAt == null)
            .OrderBy(p => p.ExpiresAt)
            .Take(take)
            .ToListAsync(ct);
}

public class WorkspaceAddonRepository : GenericRepository<WorkspaceAddon>, IWorkspaceAddonRepository
{
    public WorkspaceAddonRepository(BillingDbContext db) : base(db) { }

    public async Task<IReadOnlyList<WorkspaceAddon>> GetGrantingForWorkspaceAsync(Guid workspaceId, DateTime nowUtc, CancellationToken ct = default)
        => await _dbSet.AsNoTracking()
            .Include(w => w.Addon)
            .Where(w => w.WorkspaceId == workspaceId
                && (w.Status == PackageCatalogConstants.WorkspaceAddonStatuses.Active
                    || (w.Status == PackageCatalogConstants.WorkspaceAddonStatuses.Cancelling
                        && (w.CurrentPeriodEnd == null || w.CurrentPeriodEnd > nowUtc))))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<WorkspaceAddon>> GetOpenForWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => await _dbSet.AsNoTracking()
            .Include(w => w.Addon)
            .Where(w => w.WorkspaceId == workspaceId && w.Status != PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync(ct);

    public Task<WorkspaceAddon?> GetByStripeSubscriptionIdAsync(string stripeSubscriptionId, CancellationToken ct = default)
        => _dbSet.FirstOrDefaultAsync(w => w.StripeSubscriptionId == stripeSubscriptionId, ct);

    public Task<bool> ExistsForSessionAsync(string stripeSessionId, CancellationToken ct = default)
        => _dbSet.AnyAsync(w => w.StripeSessionId == stripeSessionId, ct);

    public async Task<IReadOnlyList<CatalogItemSales>> GetSalesAsync(CancellationToken ct = default)
    {
        var rows = await _dbSet.AsNoTracking()
            .GroupBy(w => new { w.AddonId, w.Currency })
            .Select(g => new
            {
                g.Key.AddonId,
                g.Key.Currency,
                Units = g.Count(),
                Active = g.Count(w => w.Status != PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled),
                ActiveQuantity = g.Where(w => w.Status != PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled).Sum(w => (int?)w.Quantity) ?? 0,
                Revenue = g.Sum(w => w.AmountBilledTotal),
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.AddonId)
            .Select(g => new CatalogItemSales(
                g.Key,
                g.Sum(r => r.Units),
                g.Sum(r => r.Active),
                g.Sum(r => r.ActiveQuantity),
                g.Select(r => new CurrencyTotal(r.Currency, r.Revenue)).OrderBy(t => t.Currency).ToList()))
            .ToList();
    }
}

public class CouponRedemptionRepository : GenericRepository<CouponRedemption>, ICouponRedemptionRepository
{
    public CouponRedemptionRepository(BillingDbContext db) : base(db) { }

    public Task<int> CountForCouponAsync(Guid couponId, CancellationToken ct = default)
        => _dbSet.CountAsync(r => r.CouponId == couponId, ct);

    public Task<int> CountForWorkspaceAsync(Guid couponId, Guid workspaceId, CancellationToken ct = default)
        => _dbSet.CountAsync(r => r.CouponId == couponId && r.WorkspaceId == workspaceId, ct);

    public Task<bool> ExistsForSessionAsync(string stripeSessionId, CancellationToken ct = default)
        => _dbSet.AnyAsync(r => r.StripeSessionId == stripeSessionId, ct);

    public async Task<IReadOnlyList<CatalogItemSales>> GetSalesAsync(CancellationToken ct = default)
    {
        var rows = await _dbSet.AsNoTracking()
            .GroupBy(r => new { r.CouponId, r.Currency })
            .Select(g => new { g.Key.CouponId, g.Key.Currency, Units = g.Count(), Discount = g.Sum(r => r.DiscountAmount) })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.CouponId)
            .Select(g => new CatalogItemSales(
                g.Key,
                g.Sum(r => r.Units),
                0,
                0,
                g.Select(r => new CurrencyTotal(r.Currency, r.Discount)).OrderBy(t => t.Currency).ToList()))
            .ToList();
    }
}
