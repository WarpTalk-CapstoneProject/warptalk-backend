using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// What a lift check did to one subscription. <see cref="LiftedReason"/> is the suspension reason
/// that was cleared (null when nothing was lifted); <see cref="OverageSettled"/> is how many
/// overage credits of this cycle the grant paid off (see <see cref="ISuspensionLiftService"/>).
/// </summary>
public sealed record SuspensionLiftOutcome(bool Lifted, string? LiftedReason, int OverageSettled)
{
    public static readonly SuspensionLiftOutcome None = new(false, null, 0);
}

/// <summary>
/// WT-878 — THE ONE RULE for "money arrived: may this workspace resume?".
///
/// Paying must lift the suspension it paid for, and only that one:
///   * <c>overage_cap</c> lifts when a credit grant brings the balance back above zero.
///   * <c>invoice_overdue</c> lifts when an invoice is paid and the workspace has no OTHER invoice
///     the overdue sweeper would suspend it for.
///   * <c>trial_ended</c>, <c>subscription_expired</c>, and anything else (an admin suspension, an
///     unknown reason) are never lifted by a payment: each has its own way back (a plan, a
///     renewal, an admin).
///
/// The Stage* methods change the tracked subscription and never save: the caller commits them in
/// the same unit of work as the grant or the invoice settlement. The push/publish methods run
/// AFTER that commit and are best-effort, never failing money that is already recorded.
/// </summary>
public interface ISuspensionLiftService
{
    /// <summary>
    /// Credits were just added to <paramref name="subscription"/>.CreditsRemaining. Settles the
    /// overage those credits paid off and lifts an <c>overage_cap</c> suspension when the balance is
    /// positive again.
    /// </summary>
    SuspensionLiftOutcome StageAfterCreditGrant(Subscription subscription, DateTime nowUtc);

    /// <summary>
    /// Invoice <paramref name="paidInvoiceId"/> of <paramref name="subscription"/>'s workspace was
    /// just settled. Lifts an <c>invoice_overdue</c> suspension unless another invoice still holds it.
    /// </summary>
    Task<SuspensionLiftOutcome> StageAfterInvoicePaidAsync(
        Subscription subscription, Guid paidInvoiceId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>After commit: tells the AI pipeline the subscription's service state (as the expiry sweep and admin lifts do).</summary>
    Task PushServiceStateAsync(Subscription subscription, CancellationToken ct = default);

    /// <summary>
    /// After commit: publishes <c>billing.credits_updated</c> to the subscription owner.
    /// <paramref name="resumed"/> is whether the same change lifted a suspension (the message says so).
    /// </summary>
    Task PublishCreditsUpdatedAsync(Subscription subscription, bool resumed, CancellationToken ct = default);
}
