using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IPaymentRepository : IGenericRepository<Payment>
{
    Task<Payment?> GetWithSubscriptionAsync(Guid paymentId, CancellationToken cancellationToken);
    Task<Payment?> GetWithSubscriptionAndPlanAsync(Guid paymentId, CancellationToken cancellationToken);
    Task<PagedResult<Payment>> GetHistoryPageAsync(Guid subscriptionId, PageRequest page, CancellationToken cancellationToken = default);

    // ── Admin Insights (2026-09-17) ──────────────────────────────────────────
    //
    // "Counted" paid payments are the revenue source of truth: status = paid, dated by paid_at
    // (updated_at when paid_at was never stamped), soft-deleted subscriptions included — the money
    // was still received. One Stripe subscription checkout writes TWO paid rows, the checkout
    // session (cs_…) and its subscription_create invoice (in_…), for the same charge; an in_… row
    // with a cs_… twin (same user, currency and total, paid within an hour) is not counted.

    /// <summary>Counted paid payments in [from, to), per currency.</summary>
    Task<IReadOnlyList<PaymentCurrencyTotal>> GetCountedPaidTotalsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>Paid in_… rows in [from, to) left out as the duplicate of a checkout session.</summary>
    Task<int> CountStripeInvoiceDuplicatesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>Failed payments whose last update falls in [from, to).</summary>
    Task<int> CountFailedAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counted paid payments in [from, to), one row each, unbucketed. Which day or month a payment
    /// belongs to depends on the caller's time zone, so the caller buckets them.
    /// </summary>
    Task<IReadOnlyList<PaidAmountRow>> GetCountedPaidAmountsAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default);

    // ── Admin workspace page (ERP detail). The same counting rules, scoped to one workspace. ──

    /// <summary>
    /// <see cref="GetCountedPaidTotalsAsync"/> for one workspace: paid payments on any of its
    /// subscriptions, the Stripe in_… twin of a checkout session left out exactly as Insights does.
    /// </summary>
    Task<IReadOnlyList<PaymentCurrencyTotal>> GetWorkspaceCountedPaidTotalsAsync(
        Guid workspaceId, DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary><see cref="CountStripeInvoiceDuplicatesAsync"/> for one workspace.</summary>
    Task<int> CountWorkspaceStripeInvoiceDuplicatesAsync(
        Guid workspaceId, DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>Newest payments that represent a charge attempt (not pending, not subscription_updated), newest first.</summary>
    Task<IReadOnlyList<RecentPaymentRow>> GetRecentChargesAsync(int take, CancellationToken cancellationToken = default);
}
