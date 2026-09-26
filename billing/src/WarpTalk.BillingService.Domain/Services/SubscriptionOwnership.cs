using System;
using System.Linq.Expressions;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Services;

/// <summary>
/// #466 — EXACTLY ONE OWNER PER STATE TRANSITION.
///
/// Two hourly workers used to select the same rows. SubscriptionExpirationWorker took every active
/// row whose period had ended; BillingCycleWorker took every auto-renewing one. Nothing ordered
/// them, so whichever ran first won: an <c>auto_renew</c> subscription (prod row fac2906b…) was
/// expired seven minutes after its period ended instead of being renewed. And for a card customer
/// neither winner was right — the cycle close granted a cycle nobody paid for, while Stripe charged
/// the card again and the <c>invoice.paid</c> webhook granted it a second time.
///
/// The rule is now a column, <see cref="Subscription.RenewalMode"/>, and three disjoint predicates:
///
///   * <see cref="DueForCycleClose"/> — only <c>invoice</c> rows. The cycle close never touches a
///     card customer.
///   * Stripe webhooks — only rows linked to the Stripe subscription the event is about.
///   * <see cref="DueForExpiry"/> — a row whose period ended, EXCEPT while another owner may still
///     act on it: the cycle close inside its lookback, Stripe while its renewal webhook is due, and
///     any row inside its dunning grace window.
///
/// The predicates are expressions so the repositories query with them and the tests can run the
/// very same expression over plain rows. Concurrency on the SAME row (a webhook and a sweep in the
/// same second) is caught by the xmin token on subscriptions; idempotency of a renewal is the unique
/// payments.provider_transaction_id carrying the Stripe invoice id.
/// </summary>
public static class SubscriptionOwnership
{
    public static Expression<Func<Subscription, bool>> DueForCycleClose(DateTime renewalThreshold, DateTime lowerBound) =>
        s => s.IsActive
             && s.DeletedAt == null
             && s.AutoRenew
             && s.RenewalMode == SubscriptionConstants.RenewalModes.Invoice
             && s.Status == SubscriptionConstants.SubscriptionStatuses.Active
             && s.CurrentPeriodEnd <= renewalThreshold
             && s.CurrentPeriodEnd > lowerBound;

    /// <param name="now">The sweep's clock.</param>
    /// <param name="renewalLookback">BillingCycleWorker's lookback: an invoice row is the cycle
    /// close's until its period end is older than this.</param>
    /// <param name="stripeSafetyMargin">How long past its period end a Stripe-owned row with no
    /// failure recorded waits for its renewal webhook before the sweep assumes it was lost.</param>
    public static Expression<Func<Subscription, bool>> DueForExpiry(DateTime now, TimeSpan renewalLookback, TimeSpan stripeSafetyMargin)
    {
        var cycleCloseCutoff = now - renewalLookback;
        var stripeCutoff = now - stripeSafetyMargin;
        var canceled = SubscriptionConstants.StripeSubscriptionStatuses.Canceled;
        var incompleteExpired = SubscriptionConstants.StripeSubscriptionStatuses.IncompleteExpired;

        return s => s.IsActive
                    && s.DeletedAt == null
                    && s.CurrentPeriodEnd < now
                    // The cycle close still owns it.
                    && !(s.RenewalMode == SubscriptionConstants.RenewalModes.Invoice
                         && s.AutoRenew
                         && s.Status == SubscriptionConstants.SubscriptionStatuses.Active
                         && s.CurrentPeriodEnd > cycleCloseCutoff)
                    // Stripe still owns it: renewal on, no failure yet, Stripe has not ended it,
                    // and the safety margin for a late webhook has not run out.
                    && !(s.RenewalMode == SubscriptionConstants.RenewalModes.Stripe
                         && s.StripeSubscriptionId != null
                         && s.AutoRenew
                         && s.PaymentFailedAt == null
                         && (s.StripeSubscriptionStatus == null
                             || (s.StripeSubscriptionStatus != canceled && s.StripeSubscriptionStatus != incompleteExpired))
                         && s.CurrentPeriodEnd > stripeCutoff)
                    // Dunning: the plan stays in force until the grace window ends.
                    && !(s.PaymentFailedAt != null
                         && s.PaymentGraceEndsAt != null
                         && s.PaymentGraceEndsAt > now);
    }
}
