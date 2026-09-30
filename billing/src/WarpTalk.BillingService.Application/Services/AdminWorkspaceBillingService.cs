using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using static WarpTalk.BillingService.Application.Services.AdminBillingInsightsCalculator;

namespace WarpTalk.BillingService.Application.Services;

/// <inheritdoc cref="IAdminWorkspaceBillingService"/>
/// <remarks>
/// THE ORDER EVERY WRITE FOLLOWS: validate, stage the change on tracked entities, record it in the
/// platform audit log, and only then save. Billing's DbContext runs under a retrying execution
/// strategy, which refuses user-initiated transactions, so auth's "record inside an open
/// transaction" is not available here — recording first and committing in one SaveChanges is the
/// equivalent. If the save then fails, a <c>failed</c> entry follows the <c>succeeded</c> one, so the
/// trail never claims a change that did not land.
/// </remarks>
public sealed class AdminWorkspaceBillingService : IAdminWorkspaceBillingService
{
    public const int MaxReasonLength = 500;
    public const int MaxCreditAdjustment = 1_000_000;
    public const int MaxTrialExtensionDays = 90;
    public const int MaxCompPeriods = 12;
    public const int MaxRangeDays = 366;
    public const long MaxEntitlementLimit = 100_000;

    /// <summary>Lifetime revenue starts here: before the platform existed, so it is every payment.</summary>
    private static readonly DateTime LifetimeStart = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The rate-card estimate is all a single workspace can be priced at. Measured Cartesia credits
    /// (which the platform Insights page swaps in for dubbing) are one account-wide number per day,
    /// and splitting them across tenants would be an invention.
    /// </summary>
    public const string WorkspaceAiCostNote =
        "rate-card estimate: measured Cartesia usage is platform-wide and is not attributed to one workspace";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICreditService _creditService;
    private readonly IEntitlementResolver _entitlementResolver;
    private readonly IUsageRateCardRepository _pricingConfig;
    private readonly IAdminAuditRecorder _audit;
    private readonly ILogger<AdminWorkspaceBillingService> _logger;
    private readonly TimeProvider _time;
    private readonly IEntitlementChangePublisher? _entitlementChangePublisher;
    private readonly IAiServiceStateStore? _aiServiceStateStore;

    public AdminWorkspaceBillingService(
        IUnitOfWork unitOfWork,
        ICreditService creditService,
        IEntitlementResolver entitlementResolver,
        IUsageRateCardRepository pricingConfig,
        IAdminAuditRecorder audit,
        ILogger<AdminWorkspaceBillingService> logger,
        TimeProvider? timeProvider = null,
        IEntitlementChangePublisher? entitlementChangePublisher = null,
        IAiServiceStateStore? aiServiceStateStore = null)
    {
        _unitOfWork = unitOfWork;
        _creditService = creditService;
        _entitlementResolver = entitlementResolver;
        _pricingConfig = pricingConfig;
        _audit = audit;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _entitlementChangePublisher = entitlementChangePublisher;
        _aiServiceStateStore = aiServiceStateStore;
    }

    // ── Read ────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminWorkspaceBillingOverviewDto>> GetOverviewAsync(
        Guid workspaceId, AdminDateRange range, CancellationToken ct = default)
    {
        if (!range.TryNormalize(MaxRangeDays, out var from, out var to, out var error))
        {
            return Result.Failure<AdminWorkspaceBillingOverviewDto>(error!, ErrorCodes.ValidationError);
        }

        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var fx = await ReadFxAsync(ct);

            var subscription = await ReadSubscriptionAsync(workspaceId, ct);
            var plan = subscription is null ? null : subscription.Plan ?? await _unitOfWork.Plans.GetByIdAsync(subscription.PlanId, ct);

            var payments = _unitOfWork.PaymentRepository;
            var (lifetime, lifetimeTotal) = Revenue(
                await payments.GetWorkspaceCountedPaidTotalsAsync(workspaceId, LifetimeStart, now, ct),
                await payments.CountWorkspaceStripeInvoiceDuplicatesAsync(workspaceId, LifetimeStart, now, ct),
                fx);
            var (period, periodTotal) = Revenue(
                await payments.GetWorkspaceCountedPaidTotalsAsync(workspaceId, from, to, ct),
                await payments.CountWorkspaceStripeInvoiceDuplicatesAsync(workspaceId, from, to, ct),
                fx);

            var consumption = await _unitOfWork.CreditTransactionRepository.GetWorkspaceConsumptionTotalsAsync(
                workspaceId, from, to, ct);
            var aiCost = AiProviderCost(consumption, fx);
            aiCost = aiCost with { Note = JoinNotes(aiCost.Note, consumption.Transactions > 0 ? WorkspaceAiCostNote : null) };
            var margin = GrossMargin(period, aiCost, consumption);

            var outstanding = await _unitOfWork.InvoiceRepository.GetOutstandingForWorkspaceAsync(workspaceId, ct);
            var outstandingTotal = ToVnd(outstanding.Select(i => new MoneyPart(i.Currency, i.Total, 1)), fx);
            var overdue = outstanding.Where(i => i.DueAt is { } due && due < now).ToList();
            var oldestOverdue = overdue.Select(i => i.DueAt!.Value).DefaultIfEmpty().Min();

            var ledger = await _unitOfWork.CreditTransactionRepository.GetWorkspaceLedgerPointsAsync(workspaceId, from, to, ct);

            var entitlements = await ReadEntitlementsAsync(workspaceId, subscription, ct);

            return Result.Success(new AdminWorkspaceBillingOverviewDto(
                workspaceId,
                from,
                to,
                subscription is null ? null : ToSummary(subscription, plan, now),
                Money(lifetime),
                lifetimeTotal.IncludedRows,
                Money(period),
                periodTotal.IncludedRows,
                Money(aiCost),
                Money(margin),
                consumption.CreditsConsumed,
                new AdminWorkspaceInvoiceSummaryDto(
                    outstanding.Count,
                    overdue.Count,
                    new AdminWorkspaceMoneyDto(
                        outstanding.Count == 0 ? 0m : outstandingTotal.Amount,
                        Vnd,
                        ConversionNote(outstandingTotal, fx)),
                    outstanding.Where(i => i.DueAt is { } due && due >= now).Select(i => i.DueAt).Min(),
                    overdue.Count == 0 ? null : (int)Math.Floor((now - oldestOverdue).TotalDays)),
                Burn(ledger, from, to),
                entitlements));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin workspace billing overview failed. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<AdminWorkspaceBillingOverviewDto>(
                "An unexpected error occurred while building the workspace's billing overview.",
                ErrorCodes.InternalServerError);
        }
    }

    // ── Writes ──────────────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminWorkspaceBillingActionResultDto>> AdjustCreditsAsync(
        Guid workspaceId, AdminAdjustWorkspaceCreditsRequest request, AdminActorContext actor, CancellationToken ct = default)
    {
        if (ValidateReason(request?.Reason) is { } reasonError) return reasonError;
        if (request!.Amount == 0)
            return Invalid("The adjustment must be a non-zero whole number of credits.");
        if (Math.Abs((long)request.Amount) > MaxCreditAdjustment)
            return Invalid(string.Create(CultureInfo.InvariantCulture,
                $"A single adjustment cannot exceed {MaxCreditAdjustment:N0} credits."));

        var reason = request.Reason.Trim();
        var staged = await _creditService.StageWorkspaceAdjustmentAsync(
            workspaceId, request.Amount, reason, actor.ActorId, ct);
        if (!staged.IsSuccess)
        {
            _unitOfWork.ClearTracking();
            return Result.Failure<AdminWorkspaceBillingActionResultDto>(staged.Error!, NormalizeCode(staged.ErrorCode));
        }

        var (subscription, entry, balanceBefore, frozenBefore) = staged.Value!;
        var before = new Dictionary<string, string?> { ["credits_remaining"] = Invariant(balanceBefore) };
        var after = new Dictionary<string, string?>
        {
            ["credits_remaining"] = Invariant(subscription.CreditsRemaining),
            ["amount"] = Invariant(request.Amount),
            ["subscription_id"] = subscription.Id.ToString(),
        };
        if (frozenBefore is { } frozen)
        {
            // The workspace had no live subscription, so the adjustment went to the credits it
            // kept from the one that ended. Audited as such: the spendable balance did not move.
            before["frozen_credits"] = Invariant(frozen);
            after["frozen_credits"] = Invariant(subscription.FrozenCredits);
        }

        return await RecordThenSaveAsync(
            workspaceId,
            actor,
            AdminAuditWorkspaceActions.CreditAdjusted,
            AdminAuditEntityTypes.CreditAdjustment,
            entry.Id,
            reason,
            before,
            after,
            afterSave: () => Task.FromResult(new AdminWorkspaceBillingActionResultDto(
                AdminAuditWorkspaceActions.CreditAdjusted,
                ToSummary(subscription, subscription.Plan, Now()),
                ToLedgerDto(entry),
                null,
                null)),
            entitlementReason: null,
            ct);
    }

    public async Task<Result<AdminWorkspaceBillingActionResultDto>> ChangePlanAsync(
        Guid workspaceId, AdminWorkspaceChangePlanRequest request, AdminActorContext actor, CancellationToken ct = default)
    {
        if (ValidateReason(request?.Reason) is { } reasonError) return reasonError;

        var subscription = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, includePlan: true, cancellationToken: ct);
        if (subscription is null) return NoSubscription();

        if (subscription.PlanId == request!.PlanId)
            return Conflict("The subscription is already on this plan.");

        var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(p => p.Id == request.PlanId && p.DeletedAt == null, ct);
        if (plan is null)
            return Result.Failure<AdminWorkspaceBillingActionResultDto>(
                ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.NotFound);

        // A hidden plan is retired from sale; moving a customer onto one puts numbers in force that
        // no price page describes (the same rule the subscriptions page's plan move enforces).
        if (!plan.IsActive)
            return Invalid("The target plan is deactivated. Reactivate it before moving a subscription onto it.");

        var before = new Dictionary<string, string?>
        {
            ["plan_id"] = subscription.PlanId.ToString(),
            ["plan"] = subscription.Plan?.Name,
        };

        subscription.PlanId = plan.Id;
        subscription.Plan = plan;
        subscription.UpdatedAt = Now();
        subscription.UpdatedBy = actor.ActorId;
        _unitOfWork.SubscriptionRepository.Update(subscription);

        return await RecordThenSaveAsync(
            workspaceId,
            actor,
            AdminAuditWorkspaceActions.PlanChanged,
            AdminAuditEntityTypes.Subscription,
            subscription.Id,
            request.Reason.Trim(),
            before,
            new Dictionary<string, string?> { ["plan_id"] = plan.Id.ToString(), ["plan"] = plan.Name },
            afterSave: () => Task.FromResult(new AdminWorkspaceBillingActionResultDto(
                AdminAuditWorkspaceActions.PlanChanged, ToSummary(subscription, plan, Now()), null, null, null)),
            entitlementReason: EntitlementConstants.Reasons.PlanChanged,
            ct);
    }

    public async Task<Result<AdminWorkspaceBillingActionResultDto>> ExtendTrialAsync(
        Guid workspaceId, AdminExtendTrialRequest request, AdminActorContext actor, CancellationToken ct = default)
    {
        if (ValidateReason(request?.Reason) is { } reasonError) return reasonError;
        if (request!.Days is < 1 or > MaxTrialExtensionDays)
            return Invalid(string.Create(CultureInfo.InvariantCulture,
                $"A trial can be extended by 1 to {MaxTrialExtensionDays} days at a time."));

        var subscription = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, includePlan: true, cancellationToken: ct);
        if (subscription is null) return NoSubscription();

        // A paid subscription has no trial to extend. Granting it free time is a comp, which says
        // so in the audit trail instead of dressing a gift up as a trial.
        if (subscription.TrialEndsAt is not { } trialEnd)
            return Conflict("This workspace is not on a trial. Use Comp to grant a paid subscription free time.");

        var now = Now();
        var newEnd = (trialEnd > now ? trialEnd : now).AddDays(request.Days);
        var before = new Dictionary<string, string?>
        {
            ["trial_ends_at"] = Iso(trialEnd),
            ["service_state"] = subscription.ServiceState,
        };

        subscription.TrialEndsAt = newEnd;
        // A trial's period IS the trial (SubscriptionMapper.ToTrialEntity): the entitlement liveness
        // check reads CurrentPeriodEnd, so moving only TrialEndsAt would leave the paywall shut.
        subscription.CurrentPeriodEnd = newEnd;
        subscription.IsActive = true;
        subscription.Status = SubscriptionConstants.SubscriptionStatuses.Active;
        var resumed = false;
        if (subscription.ServiceState == SubscriptionConstants.ServiceStates.Suspended
            && subscription.SuspendedReason == SubscriptionConstants.SuspendedReasons.TrialEnded)
        {
            subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;
            subscription.SuspendedReason = null;
            resumed = true;
        }

        subscription.UpdatedAt = now;
        subscription.UpdatedBy = actor.ActorId;
        _unitOfWork.SubscriptionRepository.Update(subscription);

        return await RecordThenSaveAsync(
            workspaceId,
            actor,
            AdminAuditWorkspaceActions.TrialExtended,
            AdminAuditEntityTypes.Subscription,
            subscription.Id,
            request.Reason.Trim(),
            before,
            new Dictionary<string, string?>
            {
                ["trial_ends_at"] = Iso(newEnd),
                ["days"] = Invariant(request.Days),
                ["service_state"] = subscription.ServiceState,
            },
            afterSave: async () =>
            {
                if (resumed) await PushAiServiceStateAsync(subscription, ct);
                return new AdminWorkspaceBillingActionResultDto(
                    AdminAuditWorkspaceActions.TrialExtended, ToSummary(subscription, subscription.Plan, Now()), null, null, null);
            },
            entitlementReason: EntitlementConstants.Reasons.SubscriptionChanged,
            ct);
    }

    public async Task<Result<AdminWorkspaceBillingActionResultDto>> CompPeriodAsync(
        Guid workspaceId, AdminCompPeriodRequest request, AdminActorContext actor, CancellationToken ct = default)
    {
        if (ValidateReason(request?.Reason) is { } reasonError) return reasonError;
        if (request!.Periods is < 1 or > MaxCompPeriods)
            return Invalid(string.Create(CultureInfo.InvariantCulture,
                $"Comp 1 to {MaxCompPeriods} periods at a time."));

        var subscription = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, includePlan: true, cancellationToken: ct);
        if (subscription is null) return NoSubscription();

        var now = Now();
        if (subscription.TrialEndsAt is { } trialEnd && trialEnd > now)
            return Conflict("This workspace is still on a trial. Extend the trial instead of comping a period.");

        var plan = subscription.Plan ?? await _unitOfWork.Plans.GetByIdAsync(subscription.PlanId, ct);
        if (plan is null)
            return Result.Failure<AdminWorkspaceBillingActionResultDto>(
                ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.NotFound);

        var creditsPerCycle = subscription.CreditsPerCycleOverride ?? plan.CreditsPerCycle;
        var paidThrough = subscription.CurrentPeriodEnd > now ? subscription.CurrentPeriodEnd : now;
        var newEnd = paidThrough.AddMonths(request.Periods);
        var granted = checked(creditsPerCycle * request.Periods);

        var before = new Dictionary<string, string?>
        {
            ["current_period_end"] = Iso(subscription.CurrentPeriodEnd),
            ["credits_remaining"] = Invariant(subscription.CreditsRemaining),
        };

        // Free time AND the credits that time would have come with — a comped month with no
        // credits is a month the customer cannot use. No payment and no invoice are raised.
        subscription.CurrentPeriodEnd = newEnd;
        subscription.CreditsRemaining += granted;
        if (subscription.CreditsRemaining > 0
            && subscription.ServiceState == SubscriptionConstants.ServiceStates.Suspended
            && subscription.SuspendedReason == SubscriptionConstants.SuspendedReasons.OverageCap)
        {
            subscription.ServiceState = SubscriptionConstants.ServiceStates.Healthy;
            subscription.SuspendedReason = null;
        }

        subscription.UpdatedAt = now;
        subscription.UpdatedBy = actor.ActorId;
        _unitOfWork.SubscriptionRepository.Update(subscription);

        CreditTransaction? entry = null;
        if (granted > 0)
        {
            entry = new CreditTransaction
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscription.Id,
                WorkspaceId = subscription.WorkspaceId,
                UserId = actor.ActorId,
                Amount = granted,
                Type = TransactionConstants.TransactionTypes.Adjustment,
                Description = string.Create(CultureInfo.InvariantCulture,
                    $"Comp: {request.Periods} free period(s) — {request.Reason.Trim()}"),
                ReferenceType = "admin_comp",
                BalanceAfter = subscription.CreditsRemaining,
                CreatedAt = now,
            };
            await _unitOfWork.CreditTransactionRepository.AddAsync(entry, ct);
        }

        return await RecordThenSaveAsync(
            workspaceId,
            actor,
            AdminAuditWorkspaceActions.PeriodComped,
            AdminAuditEntityTypes.Subscription,
            subscription.Id,
            request.Reason.Trim(),
            before,
            new Dictionary<string, string?>
            {
                ["current_period_end"] = Iso(newEnd),
                ["periods"] = Invariant(request.Periods),
                ["credits_granted"] = Invariant(granted),
                ["credits_remaining"] = Invariant(subscription.CreditsRemaining),
            },
            afterSave: () => Task.FromResult(new AdminWorkspaceBillingActionResultDto(
                AdminAuditWorkspaceActions.PeriodComped,
                ToSummary(subscription, plan, Now()),
                entry is null ? null : ToLedgerDto(entry),
                null,
                null)),
            entitlementReason: EntitlementConstants.Reasons.SubscriptionChanged,
            ct);
    }

    public async Task<Result<AdminWorkspaceBillingActionResultDto>> SetEntitlementOverridesAsync(
        Guid workspaceId, AdminEntitlementOverridesRequest request, AdminActorContext actor, CancellationToken ct = default)
    {
        if (ValidateReason(request?.Reason) is { } reasonError) return reasonError;
        if (request!.Overrides is null || request.Overrides.Count == 0)
            return Invalid("Name at least one entitlement to override or clear.");

        var subscription = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, includePlan: true, cancellationToken: ct);
        if (subscription is null) return NoSubscription();

        var current = ReadContractOverrides(subscription.EntitlementOverrides);
        var next = new SortedDictionary<string, object>(current.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);

        foreach (var (key, value) in request.Overrides)
        {
            if (!EntitlementConstants.Keys.All.Contains(key, StringComparer.Ordinal))
                return Invalid(string.Format(CultureInfo.InvariantCulture, EntitlementConstants.Errors.UnknownEntitlementKey, key));

            if (value is null || value.Value.ValueKind == JsonValueKind.Null)
            {
                next.Remove(key);
                continue;
            }

            if (EntitlementConstants.Keys.IsNumericLimit(key))
            {
                if (value.Value.ValueKind != JsonValueKind.Number
                    || !value.Value.TryGetInt64(out var limit)
                    || limit < 0
                    || limit > MaxEntitlementLimit)
                {
                    return Invalid(string.Create(CultureInfo.InvariantCulture,
                        $"'{key}' is a limit: a whole number from 0 to {MaxEntitlementLimit:N0}."));
                }

                next[key] = limit;
            }
            else
            {
                if (value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Invalid($"'{key}' is a capability: true or false.");

                next[key] = value.Value.GetBoolean();
            }
        }

        var beforeJson = subscription.EntitlementOverrides;
        subscription.EntitlementOverrides = next.Count == 0 ? null : JsonSerializer.Serialize(next);
        subscription.UpdatedAt = Now();
        subscription.UpdatedBy = actor.ActorId;
        _unitOfWork.SubscriptionRepository.Update(subscription);

        return await RecordThenSaveAsync(
            workspaceId,
            actor,
            AdminAuditWorkspaceActions.EntitlementsOverridden,
            AdminAuditEntityTypes.Subscription,
            subscription.Id,
            request.Reason.Trim(),
            new Dictionary<string, string?> { ["entitlement_overrides"] = beforeJson ?? "{}" },
            new Dictionary<string, string?> { ["entitlement_overrides"] = subscription.EntitlementOverrides ?? "{}" },
            afterSave: async () => new AdminWorkspaceBillingActionResultDto(
                AdminAuditWorkspaceActions.EntitlementsOverridden,
                ToSummary(subscription, subscription.Plan, Now()),
                null,
                null,
                await ReadEntitlementsAsync(workspaceId, subscription, ct)),
            entitlementReason: EntitlementConstants.Reasons.ContractOverrideChanged,
            ct);
    }

    public async Task<Result<AdminWorkspaceBillingActionResultDto>> MarkInvoicePaidAsync(
        Guid workspaceId, Guid invoiceId, AdminMarkInvoicePaidRequest request, AdminActorContext actor, CancellationToken ct = default)
    {
        if (ValidateReason(request?.Reason) is { } reasonError) return reasonError;

        var invoice = await _unitOfWork.InvoiceRepository.FirstOrDefaultAsync(
            i => i.Id == invoiceId, "Payment.Subscription", ct);

        // An invoice of another workspace is "not found" from this page — the route names the
        // workspace, and settling someone else's invoice from it is the one mistake it must not allow.
        if (invoice is null || invoice.Payment?.Subscription?.WorkspaceId != workspaceId)
            return Result.Failure<AdminWorkspaceBillingActionResultDto>(
                BillingMessageConstants.ApiErrorMessages.BillingInvoiceNotFound, ErrorCodes.NotFound);

        if (invoice.Status == InvoiceConstants.InvoiceStatuses.Paid)
            return Conflict("This invoice is already paid.");
        if (invoice.Status is InvoiceConstants.InvoiceStatuses.Void or InvoiceConstants.InvoiceStatuses.Draft)
            return Conflict($"A {invoice.Status} invoice cannot be marked paid.");

        var before = new Dictionary<string, string?>
        {
            ["status"] = invoice.Status,
            ["invoice_number"] = invoice.InvoiceNumber,
            ["total"] = Invariant(invoice.Total),
            ["currency"] = invoice.Currency,
        };

        invoice.MarkPaid(Now());

        return await RecordThenSaveAsync(
            workspaceId,
            actor,
            AdminAuditWorkspaceActions.InvoiceMarkedPaid,
            AdminAuditEntityTypes.Invoice,
            invoice.Id,
            request!.Reason.Trim(),
            before,
            new Dictionary<string, string?>
            {
                ["status"] = invoice.Status,
                ["invoice_number"] = invoice.InvoiceNumber,
                ["paid_at"] = invoice.PaidAt is { } paidAt ? Iso(paidAt) : null,
            },
            afterSave: () => Task.FromResult(new AdminWorkspaceBillingActionResultDto(
                AdminAuditWorkspaceActions.InvoiceMarkedPaid, null, null, invoice.ToDto(workspaceId), null)),
            entitlementReason: null,
            ct);
    }

    // ── The ordering ────────────────────────────────────────────────────────────────────────

    private async Task<Result<AdminWorkspaceBillingActionResultDto>> RecordThenSaveAsync(
        Guid workspaceId,
        AdminActorContext actor,
        string action,
        string entityType,
        Guid? entityId,
        string reason,
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after,
        Func<Task<AdminWorkspaceBillingActionResultDto>> afterSave,
        string? entitlementReason,
        CancellationToken ct)
    {
        var recorded = await _audit.RecordAsync(
            action, entityType, entityId, workspaceId, actor.ActorId, reason, actor.CorrelationId, before, after, true, ct);
        if (!recorded.IsSuccess)
        {
            // Nothing staged may survive into a later SaveChanges on this scope.
            _unitOfWork.ClearTracking();
            _logger.LogWarning(
                "Admin billing action abandoned because it could not be audited. Action: {Action}, WorkspaceId: {WorkspaceId}",
                action, workspaceId);
            return Result.Failure<AdminWorkspaceBillingActionResultDto>(
                recorded.Error ?? "The action was not performed because it could not be audited.",
                ErrorCodes.InternalServerError);
        }

        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _unitOfWork.ClearTracking();
            _logger.LogError(ex, "Admin billing action failed to save after being recorded. Action: {Action}, WorkspaceId: {WorkspaceId}",
                action, workspaceId);

            // A separate correlation id: the store de-duplicates on (source, correlation, action,
            // entity), and this entry must not be swallowed as a repeat of the first.
            await _audit.RecordAsync(
                action, entityType, entityId, workspaceId, actor.ActorId, reason, FailedCorrelation(actor.CorrelationId),
                before, after, false, ct);
            return Result.Failure<AdminWorkspaceBillingActionResultDto>(
                "The change could not be saved, so nothing was changed. It is recorded as failed; try again.",
                ErrorCodes.Conflict);
        }

        _logger.LogInformation(
            "System admin {ActorId} performed {Action} on workspace {WorkspaceId}. CorrelationId: {CorrelationId}",
            actor.ActorId, action, workspaceId, actor.CorrelationId);

        if (entitlementReason is not null)
        {
            await EnqueueEntitlementsAsync(workspaceId, entitlementReason, ct);
        }

        return Result.Success(await afterSave());
    }

    /// <summary>
    /// After the save, the same way SubscriptionService does it: the resolver reads the committed
    /// row. Best-effort — EntitlementReconcileWorker re-publishes a snapshot that drifted.
    /// </summary>
    private async Task EnqueueEntitlementsAsync(Guid workspaceId, string reason, CancellationToken ct)
    {
        if (_entitlementChangePublisher is null) return;
        try
        {
            await _entitlementChangePublisher.EnqueueAsync(workspaceId, reason, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue entitlement change for workspace {WorkspaceId} ({Reason}).", workspaceId, reason);
        }
    }

    private async Task PushAiServiceStateAsync(Subscription subscription, CancellationToken ct)
    {
        if (_aiServiceStateStore is null) return;
        try
        {
            await _aiServiceStateStore.SetAiServiceStateAsync(
                subscription.WorkspaceId, subscription.ServiceState, subscription.SuspendedReason, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not push the AI service state for workspace {WorkspaceId}.", subscription.WorkspaceId);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task<Subscription?> ReadSubscriptionAsync(Guid workspaceId, CancellationToken ct)
    {
        var active = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, includePlan: true, cancellationToken: ct);
        if (active is not null) return active;

        // No live subscription: show the most recent one, so "expired on …" is visible rather than
        // an empty card.
        var all = await _unitOfWork.SubscriptionRepository.FindAsync(
            s => s.WorkspaceId == workspaceId && s.DeletedAt == null, "Plan", ct);
        return all.OrderByDescending(s => s.CurrentPeriodEnd).FirstOrDefault();
    }

    private async Task<IReadOnlyList<AdminWorkspaceEntitlementDto>> ReadEntitlementsAsync(
        Guid workspaceId, Subscription? subscription, CancellationToken ct)
    {
        var map = await _entitlementResolver.ResolveAsync(workspaceId, ct);
        var contract = ReadContractOverrides(subscription?.EntitlementOverrides);
        return map.Entitlements
            .Select(e => new AdminWorkspaceEntitlementDto(
                e.Key,
                e.Value,
                e.Source,
                contract.TryGetValue(e.Key, out var value) ? FormatOverride(value) : null))
            .ToList();
    }

    /// <summary>The contract layer as stored; a malformed blob reads as empty, as the resolver reads it.</summary>
    public static IReadOnlyDictionary<string, object> ReadContractOverrides(string? json)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (parsed is null) return result;
            foreach (var (key, element) in parsed)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        result[key] = element.GetBoolean();
                        break;
                    case JsonValueKind.Number when element.TryGetInt64(out var number):
                        result[key] = number;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        return result;
    }

    private static string FormatOverride(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        long number => number.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// Zero-filled UTC days of [from, to): consumed and granted credits, and the ledger's balance
    /// after the day's last row. The balance is the ledger's own number, never a running sum
    /// recomputed here — two sources for one balance is how they come to disagree.
    /// </summary>
    public static IReadOnlyList<AdminWorkspaceBurnPointDto> Burn(IReadOnlyList<LedgerPoint> ledger, DateTime from, DateTime to)
    {
        var byDay = ledger
            .GroupBy(point => DateOnly.FromDateTime(point.At))
            .ToDictionary(
                group => group.Key,
                group => (
                    Consumed: group.Where(p => p.Amount < 0).Sum(p => -(long)p.Amount),
                    Granted: group.Where(p => p.Amount > 0).Sum(p => (long)p.Amount),
                    Balance: group.OrderBy(p => p.At).Last().BalanceAfter));

        var points = new List<AdminWorkspaceBurnPointDto>();
        var last = DateOnly.FromDateTime(to.AddTicks(-1));
        for (var day = DateOnly.FromDateTime(from); day <= last; day = day.AddDays(1))
        {
            points.Add(byDay.TryGetValue(day, out var totals)
                ? new AdminWorkspaceBurnPointDto(Day(day), totals.Consumed, totals.Granted, totals.Balance)
                : new AdminWorkspaceBurnPointDto(Day(day), 0, 0, null));
        }

        return points;
    }

    private static AdminWorkspaceSubscriptionSummaryDto ToSummary(Subscription subscription, Plan? plan, DateTime now) => new(
        subscription.Id,
        subscription.PlanId,
        plan?.Name ?? "Unknown plan",
        plan?.Slug ?? string.Empty,
        plan?.BillingCycle ?? SubscriptionConstants.BillingCycles.Monthly,
        subscription.Status,
        subscription.ServiceState,
        subscription.SuspendedReason,
        subscription.TrialEndsAt is { } trialEnd && trialEnd > now,
        subscription.TrialEndsAt,
        subscription.CurrentPeriodStart,
        subscription.CurrentPeriodEnd,
        subscription.AutoRenew,
        subscription.CreditsRemaining,
        subscription.CreditsUsedThisCycle,
        subscription.CreditsPerCycleOverride ?? plan?.CreditsPerCycle ?? 0,
        subscription.FrozenCredits,
        subscription.CreditsFrozenAt,
        subscription.FrozenCreditsDormantAt);

    private static AdminCreditTransactionDto ToLedgerDto(CreditTransaction tx) => new(
        tx.Id, tx.CreatedAt, tx.Type, tx.Description, tx.ReferenceId, tx.ReferenceType, tx.Amount, tx.BalanceAfter, tx.Currency, tx.Status);

    private static AdminWorkspaceMoneyDto Money(MetricSide side) => new(side.Value, Vnd, side.Note);

    private async Task<decimal?> ReadFxAsync(CancellationToken ct)
    {
        var fx = await _pricingConfig.ReadPricingConfigValueAsync(FxRateConfigKey, 0m, ct);
        return fx > 0 ? fx : null;
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    /// <summary>The audit log's correlation column is varchar(100); keep the suffix inside it.</summary>
    public static string FailedCorrelation(string correlationId)
    {
        const string suffix = ":failed";
        const int max = 100;
        var head = correlationId.Length + suffix.Length > max ? correlationId[..(max - suffix.Length)] : correlationId;
        return head + suffix;
    }

    private static string? JoinNotes(string? first, string? second)
        => first is null ? second : second is null ? first : $"{first}; {second}";

    private static string Invariant(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);

    private static string Iso(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Result<AdminWorkspaceBillingActionResultDto>? ValidateReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return Invalid("A reason is required. It is the only record of why this was done.");
        if (trimmed.Length > MaxReasonLength)
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"The reason must be at most {MaxReasonLength} characters."));
        return null;
    }

    private static Result<AdminWorkspaceBillingActionResultDto> Invalid(string message)
        => Result.Failure<AdminWorkspaceBillingActionResultDto>(message, ErrorCodes.ValidationError);

    private static Result<AdminWorkspaceBillingActionResultDto> Conflict(string message)
        => Result.Failure<AdminWorkspaceBillingActionResultDto>(message, ErrorCodes.Conflict);

    private static Result<AdminWorkspaceBillingActionResultDto> NoSubscription()
        => Result.Failure<AdminWorkspaceBillingActionResultDto>(
            ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, ErrorCodes.NotFound);

    /// <summary>The credit service's codes, folded onto the four this controller maps.</summary>
    private static string NormalizeCode(string? code) => code switch
    {
        ErrorCodes.BillingSubscriptionNotFound => ErrorCodes.NotFound,
        ErrorCodes.BillingInsufficientCredits => ErrorCodes.Conflict,
        "INVALID_REQUEST" => ErrorCodes.ValidationError,
        null => ErrorCodes.InternalServerError,
        _ => code,
    };
}
