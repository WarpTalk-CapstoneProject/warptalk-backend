using WarpTalk.BillingService.Domain.Constants;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class PaymentRepository : GenericRepository<Payment>, IPaymentRepository
{
    public PaymentRepository(BillingDbContext context) : base(context)
    {
    }

    public async Task<Payment?> GetWithSubscriptionAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        return await _dbSet
            .Include(p => p.Subscription)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
    }

    public async Task<Payment?> GetWithSubscriptionAndPlanAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        return await _dbSet
            .Include(p => p.Subscription)
                .ThenInclude(s => s.Plan)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
    }

    public async Task<PagedResult<Payment>> GetHistoryPageAsync(Guid subscriptionId, PageRequest page, CancellationToken cancellationToken = default)
    {
        var normalized = RepositoryPaging.Normalize(page);
        var filtered = _dbSet.Where(p => p.SubscriptionId == subscriptionId);

        var total = await filtered.CountAsync(cancellationToken);
        var items = await filtered
            .OrderByDescending(p => p.CreatedAt)
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Payment>(items, total, normalized.PageNumber, normalized.PageSize);
    }

    // ── Admin Insights (2026-09-17) ──────────────────────────────────────────
    //
    // Soft-delete query filters are ignored on purpose: a payment on a since-deleted subscription is
    // still money the platform received.

    public async Task<IReadOnlyList<PaymentCurrencyTotal>> GetCountedPaidTotalsAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var rows = await CountedPaidIn(from, to)
            .GroupBy(p => p.Currency.ToUpper())
            .Select(g => new { Currency = g.Key, Payments = g.Count(), Total = g.Sum(p => p.TotalAmount) })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new PaymentCurrencyTotal(r.Currency, r.Payments, r.Total)).ToList();
    }

    public async Task<int> CountStripeInvoiceDuplicatesAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var paid = await PaidIn(from, to).CountAsync(cancellationToken);
        var counted = await CountedPaidIn(from, to).CountAsync(cancellationToken);
        return paid - counted;
    }

    public Task<int> CountFailedAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => _dbSet.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(
                p => p.Status == PaymentConstants.PaymentStatuses.Failed && p.UpdatedAt >= from && p.UpdatedAt < to,
                cancellationToken);

    public async Task<IReadOnlyList<PaidAmountRow>> GetCountedPaidAmountsAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        // Anonymous projection, mapped to the record in memory — never a record the database is
        // asked to shape. Three scalars per paid payment; bucketing by local day happens in the
        // calculator because a UTC date_trunc would put a Vietnam evening on the wrong day.
        var rows = await CountedPaidIn(from, to)
            .Select(p => new
            {
                At = p.PaidAt ?? p.UpdatedAt,
                Currency = p.Currency.ToUpper(),
                p.TotalAmount,
                // For the profit-and-loss report: who paid, and on which plan.
                WorkspaceId = (Guid?)p.Subscription.WorkspaceId,
                PlanId = (Guid?)p.Subscription.PlanId,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new PaidAmountRow(DateTime.SpecifyKind(r.At, DateTimeKind.Utc), r.Currency, r.TotalAmount, r.WorkspaceId, r.PlanId))
            .ToList();
    }

    public async Task<IReadOnlyList<RecentPaymentRow>> GetRecentChargesAsync(int take, CancellationToken cancellationToken = default)
    {
        var rows = await WithoutStripeInvoiceDuplicates(
                _dbSet.IgnoreQueryFilters().AsNoTracking().Where(p =>
                    p.Status != PaymentConstants.PaymentStatuses.Pending
                    && p.Status != PaymentConstants.PaymentStatuses.SubscriptionUpdated))
            .OrderByDescending(p => p.PaidAt ?? p.UpdatedAt)
            .ThenByDescending(p => p.Id)
            .Take(take)
            .Select(p => new
            {
                p.Id,
                WorkspaceId = (Guid?)p.Subscription.WorkspaceId,
                p.TotalAmount,
                p.Currency,
                p.Status,
                p.Provider,
                p.PaymentMethod,
                At = p.PaidAt ?? p.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new RecentPaymentRow(r.Id, r.WorkspaceId, r.TotalAmount, r.Currency, r.Status, r.Provider, r.PaymentMethod, r.At))
            .ToList();
    }

    private IQueryable<Payment> PaidIn(DateTime from, DateTime to)
        => _dbSet.IgnoreQueryFilters().AsNoTracking().Where(p =>
            p.Status == PaymentConstants.PaymentStatuses.Paid
            && (p.PaidAt ?? p.UpdatedAt) >= from
            && (p.PaidAt ?? p.UpdatedAt) < to);

    private IQueryable<Payment> CountedPaidIn(DateTime from, DateTime to)
        => WithoutStripeInvoiceDuplicates(PaidIn(from, to));

    /// <summary>
    /// Drops a paid Stripe invoice row (in_…) that has a paid checkout-session twin (cs_…).
    ///
    /// A subscription-mode Checkout fires checkout.session.completed AND invoice.paid
    /// (billing_reason subscription_create). StripeWebhookService records each under its own
    /// provider id — the session id and the invoice id — so one charge becomes two paid rows. The
    /// session row is the one the return page and the grant already key on, so it is kept. The
    /// twin is matched on what both events copy from the same checkout: the buyer (metadata
    /// UserId), the currency and the total, paid within an hour of each other. A renewal invoice
    /// (subscription_cycle) has no session twin and is always counted.
    /// </summary>
    private IQueryable<Payment> WithoutStripeInvoiceDuplicates(IQueryable<Payment> source)
    {
        var all = _dbSet.IgnoreQueryFilters();
        return source.Where(p => !(
            p.Status == PaymentConstants.PaymentStatuses.Paid
            && p.ProviderTransactionId != null
            && p.ProviderTransactionId.StartsWith(PaymentConstants.StripePrefixes.Invoice)
            && all.Any(c =>
                c.Status == PaymentConstants.PaymentStatuses.Paid
                && c.ProviderTransactionId != null
                && c.ProviderTransactionId.StartsWith(PaymentConstants.StripePrefixes.Session)
                && c.UserId == p.UserId
                && c.TotalAmount == p.TotalAmount
                && c.Currency.ToUpper() == p.Currency.ToUpper()
                && (c.PaidAt ?? c.UpdatedAt) >= (p.PaidAt ?? p.UpdatedAt).AddHours(-1)
                && (c.PaidAt ?? c.UpdatedAt) <= (p.PaidAt ?? p.UpdatedAt).AddHours(1))));
    }
}
