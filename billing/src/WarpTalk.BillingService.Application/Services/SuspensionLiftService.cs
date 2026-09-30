using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

/// <inheritdoc cref="ISuspensionLiftService"/>
/// <remarks>
/// WHERE THE RULES COME FROM
///   The overage rule: credits that bring the balance back above zero lift an overage suspension,
///   and nothing else, plus the counter settlement below. <see cref="ApplyCreditGrant"/> is the one
///   implementation: top-ups and credit packs stage it through this service, and
///   AdminWorkspaceBillingService.CompPeriodAsync and CreditFreezeService.StageReleaseIntoAsync call
///   it directly (a comp does not reset the cycle, and the frozen-credit sweep can release mid-cycle,
///   so both need the settlement as much as a top-up does).
///
/// WHY THE OVERAGE COUNTER HAS TO MOVE (subscription.settle_usage_charge, migration 20260926090000)
///   Settlement computes <c>v_new_overage = overage_credits_this_cycle + delta</c>, where delta is
///   only the part of a charge that goes BELOW zero (0 while the balance covers it), and then:
///     * v_new_overage &gt; cap              -&gt; refused, suspended 'overage_cap'
///     * v_new_overage = cap AND cap &gt; 0  -&gt; applied, suspended 'overage_cap'
///   A top-up raises the balance but, before WT-878, never touched the counter. A workspace
///   suspended at exactly the cap therefore went straight back to 'overage_cap' on its first
///   charge after paying (new_overage = cap + 0), and the ai billing_worker kept the room stopped.
///
///   The counter is "overage credits used this cycle and not paid for", and a negative balance is
///   exactly that debt: every overage credit took the balance one below zero, and RenewCycle wipes a
///   negative balance while BillingCycleCharge invoices the counter at cycle close. Credits granted
///   while the balance is negative pay that debt off first. So after a grant the counter is capped at
///   what is still owed: <c>min(counter, max(0, -balance))</c>. Positive balance -&gt; 0, so the next
///   charge settles as healthy/low_balance, and the cycle-close invoice does not bill a second time
///   for overage the customer already covered with bought credits.
/// </remarks>
public sealed class SuspensionLiftService : ISuspensionLiftService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SuspensionLiftService> _logger;
    private readonly IAiServiceStateStore? _aiServiceStateStore;
    private readonly IBillingMessagePublisher? _messagePublisher;

    public SuspensionLiftService(
        IUnitOfWork unitOfWork,
        ILogger<SuspensionLiftService> logger,
        IAiServiceStateStore? aiServiceStateStore = null,
        IBillingMessagePublisher? messagePublisher = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _aiServiceStateStore = aiServiceStateStore;
        _messagePublisher = messagePublisher;
    }

    /// <summary>The credit-grant rule as a pure function over the (already credited) subscription.</summary>
    public static SuspensionLiftOutcome ApplyCreditGrant(Subscription subscription, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        // Settle first: the lift below and the next settlement both read the counter.
        var owed = Math.Max(0, -subscription.CreditsRemaining);
        var settled = 0;
        if (subscription.OverageCreditsThisCycle > owed)
        {
            settled = subscription.OverageCreditsThisCycle - owed;
            subscription.OverageCreditsThisCycle = owed;
            if (owed == 0)
            {
                // Mirrors settle_usage_charge: overage_started_at is NULL whenever the counter is 0.
                subscription.OverageStartedAt = null;
            }

            subscription.UpdatedAt = nowUtc;
        }

        if (subscription.CreditsRemaining > 0 && IsSuspendedFor(subscription, SubscriptionConstants.SuspendedReasons.OverageCap))
        {
            Resume(subscription, nowUtc);
            return new SuspensionLiftOutcome(true, SubscriptionConstants.SuspendedReasons.OverageCap, settled);
        }

        return new SuspensionLiftOutcome(false, null, settled);
    }

    public SuspensionLiftOutcome StageAfterCreditGrant(Subscription subscription, DateTime nowUtc)
    {
        var outcome = ApplyCreditGrant(subscription, nowUtc);
        if (outcome.Lifted || outcome.OverageSettled > 0)
        {
            _logger.LogInformation(
                "suspension_lift_credit_grant WorkspaceId={WorkspaceId} SubscriptionId={SubscriptionId} Lifted={Lifted} "
                + "Reason={Reason} OverageSettled={OverageSettled} Balance={Balance}",
                subscription.WorkspaceId, subscription.Id, outcome.Lifted, outcome.LiftedReason,
                outcome.OverageSettled, subscription.CreditsRemaining);
        }

        return outcome;
    }

    public async Task<SuspensionLiftOutcome> StageAfterInvoicePaidAsync(
        Subscription subscription, Guid paidInvoiceId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (!IsSuspendedFor(subscription, SubscriptionConstants.SuspendedReasons.InvoiceOverdue))
        {
            return SuspensionLiftOutcome.None;
        }

        // "Overdue" exactly as InvoiceOverdueSweeper suspends for it: due, past the plan's grace
        // hours, and neither paid nor void. Anything looser and the sweeper would suspend again on
        // its next tick; anything stricter and a still-unpaid invoice would be forgiven. The invoice
        // just settled is excluded by id — its new status is staged, not yet in the database.
        var workspaceId = subscription.WorkspaceId;
        var candidates = await _unitOfWork.InvoiceRepository.FindAsync(
            i => i.Id != paidInvoiceId
                 && i.Payment.Subscription.WorkspaceId == workspaceId
                 && i.DueAt != null
                 && i.DueAt < nowUtc
                 && i.Status != InvoiceConstants.InvoiceStatuses.Paid
                 && i.Status != InvoiceConstants.InvoiceStatuses.Void,
            "Payment.Subscription.Plan",
            ct);

        var stillOverdue = (candidates ?? [])
            .Where(i => i.Id != paidInvoiceId && i.Status != InvoiceConstants.InvoiceStatuses.Paid)
            .Count(i => i.DueAt!.Value.AddHours(i.Payment?.Subscription?.Plan?.InvoiceGraceHours ?? 0) < nowUtc);

        if (stillOverdue > 0)
        {
            _logger.LogInformation(
                "suspension_kept_invoice_overdue WorkspaceId={WorkspaceId} PaidInvoiceId={InvoiceId} OtherOverdue={Count}",
                workspaceId, paidInvoiceId, stillOverdue);
            return SuspensionLiftOutcome.None;
        }

        Resume(subscription, nowUtc);
        _logger.LogInformation(
            "suspension_lift_invoice_paid WorkspaceId={WorkspaceId} SubscriptionId={SubscriptionId} PaidInvoiceId={InvoiceId}",
            workspaceId, subscription.Id, paidInvoiceId);
        return new SuspensionLiftOutcome(true, SubscriptionConstants.SuspendedReasons.InvoiceOverdue, 0);
    }

    public async Task PushServiceStateAsync(Subscription subscription, CancellationToken ct = default)
    {
        // The live subscription is what the AI key describes; an ended row must not overwrite it.
        if (_aiServiceStateStore is null || !subscription.IsActive)
        {
            return;
        }

        try
        {
            var pushed = await _aiServiceStateStore.SetAiServiceStateAsync(
                subscription.WorkspaceId, subscription.ServiceState, subscription.SuspendedReason, ct);
            if (!pushed.IsSuccess)
            {
                _logger.LogWarning(
                    "suspension_lift_ai_state_push_failed WorkspaceId={WorkspaceId} Error={Error}",
                    subscription.WorkspaceId, pushed.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "suspension_lift_ai_state_push_failed WorkspaceId={WorkspaceId}", subscription.WorkspaceId);
        }
    }

    public Task PublishCreditsUpdatedAsync(Subscription subscription, bool resumed, CancellationToken ct = default)
    {
        if (_messagePublisher is null)
        {
            return Task.CompletedTask;
        }

        return BillingNotificationHelper.PublishCreditsUpdatedAsync(_messagePublisher, _logger, subscription, resumed, ct);
    }

    private static bool IsSuspendedFor(Subscription subscription, string reason)
        => subscription.ServiceState == SubscriptionConstants.ServiceStates.Suspended
           && string.Equals(subscription.SuspendedReason, reason, StringComparison.Ordinal);

    /// <summary>
    /// The state a lifted subscription resumes in. settle_usage_charge recomputes it on the next
    /// charge; until then a negative balance is honestly "in overage", anything else healthy (the
    /// state CompPeriodAsync and the frozen-credit release resume into).
    /// </summary>
    private static void Resume(Subscription subscription, DateTime nowUtc)
    {
        subscription.ServiceState = subscription.CreditsRemaining < 0
            ? SubscriptionConstants.ServiceStates.InOverage
            : SubscriptionConstants.ServiceStates.Healthy;
        subscription.SuspendedReason = null;
        subscription.UpdatedAt = nowUtc;
    }
}
