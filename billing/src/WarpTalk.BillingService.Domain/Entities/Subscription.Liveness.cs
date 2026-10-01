using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class Subscription
{
    /// <summary>
    /// Whether this subscription puts its PLAN'S NUMBERS IN FORCE right now.
    ///
    /// WT-430. This test existed twice, spelled out identically in EntitlementResolver.GatherAsync
    /// and GrpcBillingMapper.ToFeatureAccessResponse, with a comment in one asking the reader to
    /// keep them in step by hand. They are the same question and now have one answer.
    ///
    /// All three conditions are load-bearing, and production proved it: a demo workspace carried
    /// <c>is_active = true</c>, a period ending three weeks out, and <c>status = 'cancelled'</c>.
    /// Exactly one of the three failed, so every entitlement fell to the platform floor — 5 rooms,
    /// 2 participants, voice clone and the assistant off — under an Enterprise label. Testing any
    /// two of the three would have called that subscription live.
    ///
    /// NOT the same question as "which subscription do I bill or credit". SubscriptionService's
    /// LoadActiveSubscriptionAsync deliberately asks a broader one — a cancelled subscription still
    /// inside its paid period keeps its credits until the period ends — and is left alone on
    /// purpose. Entitlements and money are different questions with different answers.
    /// </summary>
    ///
    /// #466: a card renewal that failed keeps the plan in force for the dunning grace window. The
    /// paid-through date does not move (no money arrived), so without this clause the workspace
    /// would drop to the platform floor the instant its period ended while Stripe is still retrying
    /// the card — the grace would exist on paper only.
    ///
    /// WT-878: a row cancelled THE OLD WAY also grants until its period ends. Before WT-878 the
    /// legacy cancel-at-period-end (SubscriptionMapper.Cancel) stamped <c>status = cancelled</c> on a
    /// row that stayed <c>is_active</c> with <c>cancelled_at</c> null; it now only turns AutoRenew
    /// off. Rows written before that change would otherwise lose the plan mid-period they paid
    /// for. No data migration: they are recognised here and the expiry sweep
    /// (SubscriptionOwnership.DueForExpiry, which does not look at the status) ends them at
    /// <c>CurrentPeriodEnd</c>. A refund or a Stripe-side cancellation stamps <c>cancelled_at</c>
    /// (CancellationPaymentEventHandler), and so does every immediate end, so those never qualify.
    public bool GrantsPlanEntitlements(DateTime nowUtc) =>
        IsActive
        && ((Status == SubscriptionConstants.SubscriptionStatuses.Active
             && (CurrentPeriodEnd >= nowUtc || IsInPaymentGrace(nowUtc)))
            || IsLegacyCancelledInPeriod(nowUtc));

    /// <summary>WT-878: cancelled-at-period-end the pre-WT-878 way, and the period has not ended.</summary>
    public bool IsLegacyCancelledInPeriod(DateTime nowUtc) =>
        IsActive
        && Status == SubscriptionConstants.SubscriptionStatuses.Cancelled
        && CancelledAt == null
        && CurrentPeriodEnd >= nowUtc;

    /// <summary>#466: a renewal charge failed and the dunning grace window has not run out.</summary>
    public bool IsInPaymentGrace(DateTime nowUtc) =>
        PaymentFailedAt is not null
        && PaymentGraceEndsAt is { } graceEnd
        && graceEnd >= nowUtc;

    /// <summary>
    /// #466: Stripe charges this row each cycle, so only Stripe's webhooks may renew it. Ownership
    /// is decided by the column, not by whether a Stripe id happens to be present, so a row that was
    /// switched to invoicing keeps its old id for the audit trail without Stripe owning it.
    /// </summary>
    public bool IsStripeManaged =>
        RenewalMode == SubscriptionConstants.RenewalModes.Stripe
        && !string.IsNullOrWhiteSpace(StripeSubscriptionId);
}
