using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

/// <inheritdoc />
public sealed class StripeSubscriptionLifecycleService : IStripeSubscriptionLifecycleService
{
    /// <summary>Returned when auto-renew is switched on for a row nothing can charge automatically.</summary>
    public const string AutoRenewRequiresCheckoutCode = "BILLING_AUTO_RENEW_REQUIRES_CHECKOUT";

    public const string AutoRenewRequiresCheckoutMessage =
        "This plan was paid once, so there is no card on file to renew it with. Choose the plan again with auto-renew on.";

    public const string StripeSubscriptionEndedMessage =
        "This plan's automatic payments have already ended in Stripe. Choose the plan again to renew it.";

    public const string NoPaymentMethodMessage = "There is no card on file for this workspace.";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IStripeRecurringGateway _stripe;
    private readonly IStripePaymentService _payments;
    private readonly ILogger<StripeSubscriptionLifecycleService> _logger;
    private readonly IEntitlementChangePublisher? _entitlements;

    public StripeSubscriptionLifecycleService(
        IUnitOfWork unitOfWork,
        IStripeRecurringGateway stripe,
        IStripePaymentService payments,
        ILogger<StripeSubscriptionLifecycleService> logger,
        IEntitlementChangePublisher? entitlements = null)
    {
        _unitOfWork = unitOfWork;
        _stripe = stripe;
        _payments = payments;
        _logger = logger;
        _entitlements = entitlements;
    }

    // ---- Auto-renew toggle -------------------------------------------------------------------

    /// <summary>
    /// Off: Stripe <c>cancel_at_period_end = true</c> — the paid period runs to its end, nothing
    /// is charged again, and the row stays ACTIVE (its plan in force) until then. On: resume —
    /// <c>cancel_at_period_end = false</c>. Stripe is asked FIRST: if it refuses, the row is left as
    /// it was, so the page can never say "renews" about a subscription Stripe will not charge.
    /// </summary>
    public async Task<Result<SubscriptionDto>> SetAutoRenewAsync(Guid workspaceId, bool autoRenew, CancellationToken ct = default)
    {
        var sub = await LoadLiveAsync(workspaceId, ct);
        if (sub is null)
        {
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);
        }

        var now = DateTime.UtcNow;
        if (sub.IsStripeManaged)
        {
            if (autoRenew && sub.StripeSubscriptionStatus is { } status
                && SubscriptionConstants.StripeSubscriptionStatuses.Ended.Contains(status))
            {
                return Result.Failure<SubscriptionDto>(StripeSubscriptionEndedMessage, AutoRenewRequiresCheckoutCode);
            }

            var updated = await _stripe.SetCancelAtPeriodEndAsync(sub.StripeSubscriptionId!, cancelAtPeriodEnd: !autoRenew, ct);
            if (!updated.IsSuccess)
            {
                _logger.LogError(
                    "auto_renew_toggle_stripe_failed WorkspaceId={WorkspaceId} StripeSubscription={StripeSubscriptionId} AutoRenew={AutoRenew} Error={Error}",
                    workspaceId, sub.StripeSubscriptionId, autoRenew, updated.Error);
                return Result.Failure<SubscriptionDto>(
                    updated.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
                    ErrorCodes.BillingExternalServiceError);
            }

            sub.StripeSubscriptionStatus = updated.Value!.Status;
            if (!string.IsNullOrWhiteSpace(updated.Value.CustomerId))
            {
                sub.StripeCustomerId = updated.Value.CustomerId;
            }
        }
        else if (autoRenew && sub.RenewalMode != SubscriptionConstants.RenewalModes.Invoice)
        {
            // A one-off purchase or a trial: there is no saved card and no Stripe subscription, so
            // "renew automatically" cannot be promised by flipping a flag.
            return Result.Failure<SubscriptionDto>(AutoRenewRequiresCheckoutMessage, AutoRenewRequiresCheckoutCode);
        }

        ApplyAutoRenew(sub, autoRenew, now);
        _unitOfWork.SubscriptionRepository.Update(sub);
        await _unitOfWork.SaveChangesAsync(ct);
        await PublishEntitlementsAsync(sub.WorkspaceId, ct);

        _logger.LogInformation(
            "auto_renew_set WorkspaceId={WorkspaceId} Subscription={SubscriptionId} RenewalMode={RenewalMode} AutoRenew={AutoRenew}",
            workspaceId, sub.Id, sub.RenewalMode, autoRenew);

        var plan = sub.Plan ?? await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, ct);
        return Result.Success(plan is null ? sub.ToDto(BillingMessageConstants.Subscription.UnknownPlan, 0m) : sub.ToDto(plan));
    }

    private static void ApplyAutoRenew(Subscription sub, bool autoRenew, DateTime now)
    {
        sub.AutoRenew = autoRenew;
        if (autoRenew)
        {
            // Also the way back from a pre-#466 cancel (Status = cancelled) inside a paid period.
            if (sub.CurrentPeriodEnd > now || sub.IsInPaymentGrace(now))
            {
                sub.Status = SubscriptionConstants.SubscriptionStatuses.Active;
            }

            sub.CancellationReason = null;
            sub.CancelledAt = null;
        }

        sub.UpdatedAt = now;
    }

    // ---- Billing page ------------------------------------------------------------------------

    public async Task<Result<RecurringBillingStatusDto>> GetRecurringBillingStatusAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var sub = await LoadLiveAsync(workspaceId, ct);
        if (sub is null)
        {
            return Result.Failure<RecurringBillingStatusDto>(ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.BillingSubscriptionNotFound);
        }

        var now = DateTime.UtcNow;
        DateTime? nextAt = null;
        decimal? nextAmount = null;
        string? nextCurrency = null;
        CardOnFileDto? card = null;
        string? stripeStatus = sub.StripeSubscriptionStatus;
        var stripeUnavailable = false;
        var customerId = sub.StripeCustomerId;

        if (sub.IsStripeManaged)
        {
            var snapshot = await _stripe.GetSubscriptionAsync(sub.StripeSubscriptionId!, ct);
            if (snapshot.IsSuccess)
            {
                var s = snapshot.Value!;
                stripeStatus = s.Status;
                customerId ??= s.CustomerId;
                nextAt = s.NextChargeAt;
                nextAmount = s.NextChargeAmount;
                nextCurrency = s.NextChargeCurrency;
                card = string.IsNullOrWhiteSpace(s.CardLast4) ? null : new CardOnFileDto(s.CardBrand, s.CardLast4, s.CardExpMonth, s.CardExpYear);
            }
            else
            {
                // The page still renders from our own row; it just cannot show the card.
                stripeUnavailable = true;
                _logger.LogWarning(
                    "recurring_status_stripe_unavailable WorkspaceId={WorkspaceId} StripeSubscription={StripeSubscriptionId} Error={Error}",
                    workspaceId, sub.StripeSubscriptionId, snapshot.Error);
                if (sub.AutoRenew)
                {
                    nextAt = sub.CurrentPeriodEnd;
                }
            }
        }
        else if (sub.RenewalMode == SubscriptionConstants.RenewalModes.Invoice && sub.AutoRenew)
        {
            // An invoice is issued at period end; the amount is the contract's (BillingCycleCharge
            // adds overage then, which is not known yet).
            var plan = sub.Plan ?? await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, ct);
            nextAt = sub.CurrentPeriodEnd;
            nextAmount = sub.ContractPriceVnd ?? plan?.Price;
            nextCurrency = sub.ContractPriceVnd is not null
                ? PaymentConstants.Currencies.Vnd
                : plan?.Currency?.ToLowerInvariant();
        }

        var inDunning = sub.PaymentFailedAt is not null;
        return Result.Success(new RecurringBillingStatusDto(
            sub.WorkspaceId,
            sub.Id,
            sub.RenewalMode,
            sub.AutoRenew,
            CanToggleAutoRenew: sub.IsStripeManaged || sub.RenewalMode == SubscriptionConstants.RenewalModes.Invoice || sub.AutoRenew,
            AutoRenewRequiresCheckout: !sub.IsStripeManaged && sub.RenewalMode != SubscriptionConstants.RenewalModes.Invoice,
            sub.CurrentPeriodEnd,
            nextAt,
            nextAmount,
            nextCurrency,
            card,
            CanManagePaymentMethod: !string.IsNullOrWhiteSpace(customerId) && _stripe.IsConfigured,
            PaymentFailed: inDunning,
            sub.PaymentFailedAt,
            sub.PaymentGraceEndsAt,
            sub.PaymentFailureReason,
            stripeStatus,
            stripeUnavailable));
    }

    /// <summary>
    /// A Stripe billing-portal session on the workspace's customer, where the owner replaces the
    /// card. The caller only picks a PATH; the gateway puts it on our own origin.
    /// </summary>
    public async Task<Result<BillingPortalDto>> CreateBillingPortalAsync(Guid workspaceId, string? returnPath, CancellationToken ct = default)
    {
        var sub = await LoadLiveAsync(workspaceId, ct);
        var customerId = sub?.StripeCustomerId;
        if (sub is not null && string.IsNullOrWhiteSpace(customerId) && sub.IsStripeManaged)
        {
            var snapshot = await _stripe.GetSubscriptionAsync(sub.StripeSubscriptionId!, ct);
            customerId = snapshot.IsSuccess ? snapshot.Value!.CustomerId : null;
            if (!string.IsNullOrWhiteSpace(customerId))
            {
                sub.StripeCustomerId = customerId;
                await _unitOfWork.SaveChangesAsync(ct);
            }
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Result.Failure<BillingPortalDto>(NoPaymentMethodMessage, ErrorCodes.BillingSubscriptionNotFound);
        }

        var portal = await _stripe.CreateBillingPortalUrlAsync(customerId, returnPath, ct);
        if (!portal.IsSuccess)
        {
            _logger.LogError("billing_portal_failed WorkspaceId={WorkspaceId} Error={Error}", workspaceId, portal.Error);
            return Result.Failure<BillingPortalDto>(portal.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.BillingExternalServiceError);
        }

        return Result.Success(new BillingPortalDto(portal.Value!));
    }

    // ---- customer.subscription.updated / .deleted --------------------------------------------

    /// <summary>
    /// Keeps the row in step with what Stripe says about renewal. An UPDATE mirrors
    /// <c>cancel_at_period_end</c> into AutoRenew (the owner may have switched it in the Stripe
    /// portal) and Stripe's status. A DELETE means Stripe will never charge again: the row keeps
    /// its paid period and ends at period end — the expiry sweep owns it from here, because the
    /// Stripe status is now <c>canceled</c> (SubscriptionOwnership.DueForExpiry).
    /// Money never moves here; renewal and dunning are invoice events.
    /// </summary>
    public async Task<Result> ApplyStripeSubscriptionChangeAsync(StripeSubscriptionChange change, CancellationToken ct = default)
    {
        var sub = await _unitOfWork.SubscriptionRepository.GetByStripeSubscriptionIdAsync(change.StripeSubscriptionId, ct);
        if (sub is null)
        {
            if (!change.Deleted || !Guid.TryParse(change.WorkspaceIdStr, out var workspaceId))
            {
                return Result.Success();
            }

            // A pre-#466 plan (its Stripe subscription was never linked) ended in Stripe. The
            // workspace's live card row stops renewing and ends at period end — it no longer loses
            // its plan the moment the event arrives. An invoice (contract) row is not Stripe's.
            sub = await LoadLiveAsync(workspaceId, ct);
            if (sub is null || sub.StripeSubscriptionId is not null || sub.RenewalMode == SubscriptionConstants.RenewalModes.Invoice)
            {
                return Result.Success();
            }
        }

        var now = DateTime.UtcNow;
        var wasAutoRenew = sub.AutoRenew;
        if (sub.StripeSubscriptionId is not null)
        {
            sub.StripeSubscriptionStatus = change.Deleted ? SubscriptionConstants.StripeSubscriptionStatuses.Canceled : change.Status;
        }

        if (!string.IsNullOrWhiteSpace(change.CustomerId))
        {
            sub.StripeCustomerId = change.CustomerId;
        }

        if (change.Deleted)
        {
            sub.AutoRenew = false;
            if (sub.IsActive)
            {
                sub.CancelledAt ??= now;
            }
        }
        else if (sub.IsActive)
        {
            ApplyAutoRenew(sub, !change.CancelAtPeriodEnd, now);
        }

        sub.UpdatedAt = now;
        _unitOfWork.SubscriptionRepository.Update(sub);
        await _unitOfWork.SaveChangesAsync(ct);
        if (sub.IsActive && wasAutoRenew != sub.AutoRenew)
        {
            await PublishEntitlementsAsync(sub.WorkspaceId, ct);
        }

        _logger.LogInformation(
            "stripe_subscription_change_applied WorkspaceId={WorkspaceId} Subscription={SubscriptionId} StripeSubscription={StripeSubscriptionId} Status={Status} Deleted={Deleted} AutoRenew={AutoRenew}",
            sub.WorkspaceId, sub.Id, change.StripeSubscriptionId, change.Status, change.Deleted, sub.AutoRenew);
        return Result.Success();
    }

    // ---- Admin backfill ----------------------------------------------------------------------

    /// <summary>
    /// Links card rows bought before #466 to the Stripe subscription their checkout created, so
    /// Stripe — not the expiry sweep — owns their renewal from the next cycle on. Reads Stripe only
    /// (checkout session → subscription); never creates, changes or cancels anything there.
    /// A row whose checkout was a one-off payment stays as it is.
    /// </summary>
    public async Task<Result<StripeLinkBackfillResultDto>> BackfillStripeLinksAsync(bool dryRun, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var rows = await _unitOfWork.SubscriptionRepository.GetUnlinkedCardSubscriptionsAsync(limit, ct);
        var outcomes = new List<StripeLinkBackfillRow>();
        int linked = 0, oneOff = 0, skipped = 0;

        foreach (var sub in rows)
        {
            ct.ThrowIfCancellationRequested();
            var payments = await _unitOfWork.PaymentRepository.FindAsync(
                p => p.SubscriptionId == sub.Id && p.Provider == PaymentConstants.Providers.Stripe,
                ct);
            var sessionId = payments
                .Where(p => p.ProviderTransactionId != null && p.ProviderTransactionId.StartsWith(PaymentConstants.StripePrefixes.Session))
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => p.ProviderTransactionId)
                .FirstOrDefault();

            if (sessionId is null)
            {
                skipped++;
                outcomes.Add(new(sub.Id, sub.WorkspaceId, "no_checkout_session", null));
                continue;
            }

            var session = await _payments.GetCheckoutSessionAsync(sessionId, ct);
            if (!session.IsSuccess)
            {
                skipped++;
                outcomes.Add(new(sub.Id, sub.WorkspaceId, "stripe_unavailable", null));
                continue;
            }

            var stripeSubscriptionId = session.Value!.SubscriptionId;
            if (string.IsNullOrWhiteSpace(stripeSubscriptionId))
            {
                oneOff++;
                outcomes.Add(new(sub.Id, sub.WorkspaceId, "one_off_payment", null));
                continue;
            }

            if (await _unitOfWork.SubscriptionRepository.GetByStripeSubscriptionIdAsync(stripeSubscriptionId, ct) is not null)
            {
                skipped++;
                outcomes.Add(new(sub.Id, sub.WorkspaceId, "already_linked_elsewhere", stripeSubscriptionId));
                continue;
            }

            var snapshot = await _stripe.GetSubscriptionAsync(stripeSubscriptionId, ct);
            if (!snapshot.IsSuccess)
            {
                skipped++;
                outcomes.Add(new(sub.Id, sub.WorkspaceId, "stripe_unavailable", stripeSubscriptionId));
                continue;
            }

            if (SubscriptionConstants.StripeSubscriptionStatuses.Ended.Contains(snapshot.Value!.Status))
            {
                skipped++;
                outcomes.Add(new(sub.Id, sub.WorkspaceId, "stripe_subscription_ended", stripeSubscriptionId));
                continue;
            }

            linked++;
            outcomes.Add(new(sub.Id, sub.WorkspaceId, dryRun ? "would_link" : "linked", stripeSubscriptionId));
            if (!dryRun)
            {
                sub.RenewalMode = SubscriptionConstants.RenewalModes.Stripe;
                sub.StripeSubscriptionId = stripeSubscriptionId;
                sub.StripeSubscriptionStatus = snapshot.Value.Status;
                sub.StripeCustomerId = snapshot.Value.CustomerId ?? session.Value.CustomerId;
                sub.AutoRenew = !snapshot.Value.CancelAtPeriodEnd;
                sub.UpdatedAt = DateTime.UtcNow;
                _unitOfWork.SubscriptionRepository.Update(sub);
                await _unitOfWork.SaveChangesAsync(ct);
            }
        }

        _logger.LogInformation(
            "stripe_link_backfill DryRun={DryRun} Examined={Examined} Linked={Linked} OneOff={OneOff} Skipped={Skipped}",
            dryRun, rows.Count, linked, oneOff, skipped);
        return Result.Success(new StripeLinkBackfillResultDto(dryRun, rows.Count, linked, oneOff, skipped, outcomes));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private Task<Subscription?> LoadLiveAsync(Guid workspaceId, CancellationToken ct) =>
        _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
            "Plan",
            ct);

    private async Task PublishEntitlementsAsync(Guid workspaceId, CancellationToken ct)
    {
        if (_entitlements is null)
        {
            return;
        }

        try
        {
            await _entitlements.EnqueueAsync(workspaceId, EntitlementConstants.Reasons.SubscriptionChanged, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "auto_renew_entitlements_publish_failed WorkspaceId={WorkspaceId}", workspaceId);
        }
    }
}
