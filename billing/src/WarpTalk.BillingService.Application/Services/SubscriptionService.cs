using WarpTalk.Shared.PlatformSettings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SubscriptionService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IStripePaymentService _stripePaymentService;
    private readonly IAiServiceStateStore? _aiServiceStateStore;
    private readonly IUsageRateCardAdminService _pricingConfigService;
    private readonly IWorkspaceClient _workspaceClient;
    private readonly IEntitlementChangePublisher? _entitlementChangePublisher;

    private readonly IPlatformSettings? _platformSettings;

    public SubscriptionService(
        IUnitOfWork unitOfWork,
        ILogger<SubscriptionService> logger,
        IBillingMessagePublisher messagePublisher,
        IStripePaymentService stripePaymentService,
        IUsageRateCardAdminService pricingConfigService,
        IWorkspaceClient workspaceClient,
        IAiServiceStateStore? aiServiceStateStore = null,
        IEntitlementChangePublisher? entitlementChangePublisher = null,
        IPlatformSettings? platformSettings = null)
    {
        _platformSettings = platformSettings;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _stripePaymentService = stripePaymentService;
        _pricingConfigService = pricingConfigService;
        _workspaceClient = workspaceClient;
        _aiServiceStateStore = aiServiceStateStore;
        _entitlementChangePublisher = entitlementChangePublisher;
    }

    /// <summary>
    /// WT-263: re-resolve and enqueue the workspace's entitlements after a subscription change.
    ///
    /// Runs AFTER the business SaveChanges, not before: the resolver reads the subscription back
    /// through this same unit of work, and an EF query does not see uncommitted changes, so
    /// resolving first would publish the values the workspace had a moment ago. The cost is a small
    /// window in which the change is committed and its event is not yet written — the backfill
    /// script in warptalk-infrastructure is the reconciliation path for that, and consumers converge
    /// on the next event regardless because the payload is a full snapshot.
    ///
    /// Never allowed to fail the caller. A subscription that was paid for must not be rolled back
    /// because an outbox insert failed; the same reconciliation path covers it.
    /// </summary>
    private async Task PublishEntitlementsAsync(Guid workspaceId, string reason, CancellationToken ct)
    {
        if (_entitlementChangePublisher is null)
        {
            return;
        }

        try
        {
            await _entitlementChangePublisher.EnqueueAsync(workspaceId, reason, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to enqueue entitlement change for workspace {WorkspaceId} ({Reason}).",
                workspaceId,
                reason);
        }
    }

    public async Task<Result<SubscriptionDto>> GetActiveSubscriptionAsync(
        Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(
                workspaceId, includePlan: false, cancellationToken: cancellationToken);

            if (sub is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);
            return Result.Success(plan is null
                ? sub.ToDto(BillingMessageConstants.Subscription.UnknownPlan, 0m)
                : sub.ToDto(plan));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorFetchingActiveSubscription, workspaceId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PaginatedResponse<SubscriptionDto>>> GetGlobalSubscriptionsAsync(
        PaginationQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _unitOfWork.SubscriptionRepository.GetPageAsync(
                BillingQueryHelper.ToPageRequest(query),
                cancellationToken);

            var items = new List<SubscriptionDto>();
            foreach (var sub in page.Items)
            {
                var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);
                items.Add(plan is null
                    ? sub.ToDto(BillingMessageConstants.PlanAuditMessages.UnknownPlan, 0m)
                    : sub.ToDto(plan));
            }

            // Resolve workspace names via workspace-service (billing must not read workspace schema)
            try
            {
                var workspaceIds = BillingQueryHelper.GetWorkspaceIds(items, i => i.WorkspaceId);

                if (workspaceIds.Length > 0)
                {
                    var namesResult = await _workspaceClient.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);
                    if (namesResult.IsSuccess)
                        items = BillingQueryHelper.ApplyWorkspaceNames(items, namesResult.Value!, i => i.WorkspaceId, (i, name) => i with { WorkspaceName = name });
                    else
                        _logger.LogWarning(BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNamesGlobalSub);
                }
            }
            catch (Exception wsEx)
            {
                _logger.LogWarning(wsEx, BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNamesGlobalSub);
            }

            return Result.Success(PaginatedResponse<SubscriptionDto>.Create(items, page.TotalCount, page.PageNumber, page.PageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorFetchingGlobalSubscriptions);
            return Result.Failure<PaginatedResponse<SubscriptionDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<SubscriptionDto>> CreateWorkspaceContractSubscriptionAsync(
        CreateWorkspaceContractSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(
                p => p.Id == request.PlanId && p.IsActive && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            var existing = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.WorkspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (existing is not null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionAlreadyActive,
                    ErrorCodes.BillingSubscriptionAlreadyActive);

            var subscription = request.ToContractSubscriptionEntity(plan);

            var pricingConfig = await GetPricingConfigAsync(cancellationToken);
            var validation = ValidateContractTerms(subscription, plan, request.ContractTerms, pricingConfig);
            if (!validation.IsSuccess)
                return Result.Failure<SubscriptionDto>(
                    validation.Error ?? BillingMessageConstants.ApiErrorMessages.BillingContractTermsInvalid,
                    validation.ErrorCode);

            subscription.ApplyContractTerms(request.ContractTerms);

            await _unitOfWork.SubscriptionRepository.AddAsync(subscription, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await PublishEntitlementsAsync(
                subscription.WorkspaceId,
                EntitlementConstants.Reasons.SubscriptionChanged,
                cancellationToken);

            await BillingNotificationHelper.PublishSubscriptionUpdateAsync(
                _messagePublisher,
                _logger,
                subscription.UserId,
                BillingMessageConstants.Notifications.ActionCreated,
                plan.Name,
                cancellationToken);

            return Result.Success(subscription.ToDto(plan));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating workspace contract subscription for WorkspaceId {WorkspaceId}", request.WorkspaceId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<SubscriptionDto>> CreateTrialSubscriptionAsync(
        TrialSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ownerDomain = EmailDomainHelper.NormalizeDomain(request.OwnerEmail);
            if (ownerDomain is null)
                return Result.Failure<SubscriptionDto>(BillingMessageConstants.ApiErrorMessages.BillingOwnerEmailInvalid, ErrorCodes.ValidationError);

            var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(
                p => p.Slug == SubscriptionConstants.PlanSlugs.Enterprise && p.IsActive && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            var existingActive = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.WorkspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (existingActive is not null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionAlreadyActive,
                    ErrorCodes.BillingSubscriptionAlreadyActive);

            var existingTrialForDomain = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.OwnerEmailDomain != null &&
                     s.OwnerEmailDomain.ToLower() == ownerDomain &&
                     s.TrialEndsAt != null &&
                     s.DeletedAt == null,
                cancellationToken);

            if (existingTrialForDomain is not null)
                return Result.Failure<SubscriptionDto>(BillingMessageConstants.ApiErrorMessages.BillingTrialAlreadyExistsForOwnerDomain, ErrorCodes.BillingSubscriptionConflict);

            // Trial length and credits from /admin/settings, read per trial; running trials keep theirs.
            var trialDays = _platformSettings is null
                ? SubscriptionConstants.TrialDefaults.DurationDays
                : await _platformSettings.GetInt32Async(PlatformSettingsCatalog.TrialDays, SubscriptionConstants.TrialDefaults.DurationDays, ct: cancellationToken);
            var trialCredits = _platformSettings is null
                ? SubscriptionConstants.TrialDefaults.Credits
                : await _platformSettings.GetInt32Async(PlatformSettingsCatalog.TrialCredits, SubscriptionConstants.TrialDefaults.Credits, ct: cancellationToken);
            var subscription = request.ToTrialEntity(plan, ownerDomain, trialDays, trialCredits);

            await _unitOfWork.SubscriptionRepository.AddAsync(subscription, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            // A trial subscription is created as part of workspace onboarding, so this is the event
            // that gives a brand-new workspace its first snapshot and takes it out of cold start.
            await PublishEntitlementsAsync(
                subscription.WorkspaceId,
                EntitlementConstants.Reasons.SubscriptionChanged,
                cancellationToken);

            await BillingNotificationHelper.PublishSubscriptionUpdateAsync(
                _messagePublisher,
                _logger,
                subscription.UserId,
                BillingMessageConstants.Notifications.ActionCreated,
                plan.Name,
                cancellationToken);

            return Result.Success(subscription.ToDto(plan));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorCreatingTrialSubscription, request.WorkspaceId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<bool>> CancelSubscriptionAsync(
        Guid workspaceId, string? reason, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(
                workspaceId, includePlan: false, cancellationToken: cancellationToken);

            if (sub is null)
                return Result.Failure<bool>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            // WT-599: cancelling an already-cancelled subscription is not a second cancellation.
            //
            // `Cancel` deliberately leaves IsActive true — the workspace keeps what it paid for
            // until the period ends — so the lookup above finds the SAME row on every later call.
            // Nothing stopped it from running again: each repeat re-stamped the row, made another
            // Stripe call, and published another notification. That is how one cancellation became
            // a column of identical "Subscription Updated" entries in the bell.
            //
            // A conflict rather than a silent success, because the caller asked for something that
            // did not happen, and "already cancelled" is what the billing page needs to say.
            if (!sub.AutoRenew || sub.Status == SubscriptionConstants.SubscriptionStatuses.Cancelled)
            {
                return Result.Failure<bool>(
                    BillingMessageConstants.ApiErrorMessages.BillingSubscriptionAlreadyCancelled,
                    ErrorCodes.BillingSubscriptionConflict);
            }

            // WT-878: Stripe is asked FIRST, and a refusal fails the whole call with the row left as
            // it was. This used to save "cancelled" locally, then call Stripe and only log a
            // warning when it failed — so the page said cancelled while the card kept being
            // charged. Same order and same rule as the #466 auto-renew toggle
            // (StripeSubscriptionLifecycleService.SetAutoRenewAsync), which this now mirrors.
            var stripeFailure = await StopStripeRenewalAsync(sub, cancellationToken);
            if (stripeFailure is not null)
            {
                return Result.Failure<bool>(stripeFailure.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, stripeFailure.ErrorCode);
            }

            if (sub.TrialEndsAt != null && !sub.IsStripeManaged)
            {
                sub.CancelImmediately(reason);
            }
            else
            {
                sub.Cancel(reason);
            }

            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await PublishEntitlementsAsync(
                sub.WorkspaceId,
                EntitlementConstants.Reasons.SubscriptionChanged,
                cancellationToken);

            var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);

            await BillingNotificationHelper.PublishSubscriptionUpdateAsync(
                _messagePublisher,
                _logger,
                sub.UserId,
                BillingMessageConstants.Notifications.ActionCancelled,
                plan?.Name ?? BillingMessageConstants.PlanAuditMessages.UnknownPlan,
                cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorCancellingSubscription, workspaceId);
            return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// WT-878: stop Stripe renewing this PLAN, before anything is written locally. Returns null
    /// when Stripe agreed (or there is nothing in Stripe to stop), otherwise the failure to return.
    ///
    ///   * Stripe-managed (#466, <see cref="Subscription.IsStripeManaged"/>): <c>cancel_at_period_end</c>
    ///     on the plan's own Stripe subscription, by its stored id. Never a metadata search — that
    ///     also matched the workspace's add-on subscriptions and cancelled them with the plan.
    ///   * Invoice (contract) rows: nothing in Stripe renews them.
    ///   * Anything else (a card plan bought before #466 whose Stripe subscription was never linked,
    ///     or a one-off purchase): the legacy search, now limited to plan subscriptions.
    /// </summary>
    private async Task<Result?> StopStripeRenewalAsync(Subscription sub, CancellationToken cancellationToken)
    {
        if (sub.RenewalMode == SubscriptionConstants.RenewalModes.Invoice && !sub.IsStripeManaged)
        {
            return null;
        }

        try
        {
            if (sub.IsStripeManaged)
            {
                var updated = await _stripePaymentService.SetPlanSubscriptionCancelAtPeriodEndAsync(
                    sub.StripeSubscriptionId!,
                    cancelAtPeriodEnd: true,
                    cancellationToken);
                if (updated is null || !updated.IsSuccess)
                {
                    _logger.LogError(
                        "legacy_cancel_stripe_failed WorkspaceId={WorkspaceId} StripeSubscription={StripeSubscriptionId} Error={Error}",
                        sub.WorkspaceId, sub.StripeSubscriptionId, updated?.Error);
                    return StripeFailure(updated?.Error);
                }

                if (!string.IsNullOrWhiteSpace(updated.Value))
                {
                    sub.StripeSubscriptionStatus = updated.Value;
                }

                return null;
            }

            var legacy = await _stripePaymentService.CancelSubscriptionAsync(sub.WorkspaceId, cancellationToken);
            if (legacy is null || !legacy.IsSuccess)
            {
                _logger.LogError(
                    "legacy_cancel_stripe_search_failed WorkspaceId={WorkspaceId} Error={Error}",
                    sub.WorkspaceId, legacy?.Error);
                return StripeFailure(legacy?.Error);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorCancellingStripeSubscription, sub.WorkspaceId);
            return StripeFailure(null);
        }
    }

    private static Result StripeFailure(string? error) =>
        Result.Failure(
            string.IsNullOrWhiteSpace(error) ? ApiMessageConstants.ErrorMessages.BillingInternalError : error,
            ErrorCodes.BillingExternalServiceError);

    /// <summary>
    /// WT-471: switch renewal back on for a subscription that was cancelled but has not expired.
    ///
    /// There was no way back into a plan from inside the product. Cancel existed, auto-renew was
    /// deliberately never built ("cũng đâu có chứng minh được trong demo"), and nothing reversed a
    /// cancellation — so every cancelled workspace was a dead end. Two reasonable decisions that
    /// together made a trap.
    ///
    /// NOT the same thing as <see cref="ResumeSubscriptionAsync"/>, which clears a ServiceState
    /// suspension caused by running past the overage cap. Those are independent axes: a
    /// subscription can be healthy and cancelled, or suspended and renewing. Reusing that endpoint
    /// would have refused every cancelled-but-healthy subscription with "AI service is not
    /// suspended", which is true and answers a question nobody asked.
    ///
    /// WT-878: Stripe used to be deliberately NOT called here, on the reasoning that un-cancelling
    /// was not symmetric. For a Stripe-managed plan it is: CancelSubscriptionAsync only sets
    /// <c>cancel_at_period_end</c>, and leaving it set meant the page said "renews" while Stripe
    /// deleted the subscription at period end. So a Stripe-managed row now clears it on the plan's
    /// own Stripe subscription first, exactly like auto-renew ON (#466), and fails without a local
    /// change if Stripe refuses. A row nothing can charge again (one-off purchase) is sent to
    /// checkout, the same "requires checkout" rule the auto-renew toggle has. It still never
    /// creates a charge: the period is already paid for.
    /// </summary>
    public async Task<Result<SubscriptionDto>> ReactivateSubscriptionAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(
                workspaceId, includePlan: false, cancellationToken: cancellationToken);

            // A trial cancelled through CancelImmediately has IsActive = false, so it does not
            // match here and correctly reads as "nothing to reactivate" rather than being revived.
            if (sub is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            // AutoRenew is the field Cancel actually flips, and CancelAtPeriodEnd on the wire is
            // literally `!AutoRenew`. Testing it — rather than the status string — keeps this
            // agreeing with what every client already renders.
            if (sub.AutoRenew)
                return Result.Failure<SubscriptionDto>(
                    BillingMessageConstants.ApiErrorMessages.BillingSubscriptionNotCancelled,
                    ErrorCodes.BillingSubscriptionConflict);

            if (sub.CurrentPeriodEnd <= DateTime.UtcNow)
                return Result.Failure<SubscriptionDto>(
                    BillingMessageConstants.ApiErrorMessages.BillingSubscriptionPeriodAlreadyEnded,
                    ErrorCodes.BillingSubscriptionConflict);

            // WT-878: the same rules as auto-renew ON (StripeSubscriptionLifecycleService.SetAutoRenewAsync).
            if (sub.IsStripeManaged)
            {
                if (sub.StripeSubscriptionStatus is { } stripeStatus
                    && SubscriptionConstants.StripeSubscriptionStatuses.Ended.Contains(stripeStatus))
                {
                    return Result.Failure<SubscriptionDto>(
                        StripeSubscriptionLifecycleService.StripeSubscriptionEndedMessage,
                        StripeSubscriptionLifecycleService.AutoRenewRequiresCheckoutCode);
                }

                // Stripe first: cancel_at_period_end=true is still set there from the cancel, and
                // leaving it would have Stripe end the subscription at period end while this page
                // says "renews". If Stripe refuses, nothing changes here.
                var resumed = await _stripePaymentService.SetPlanSubscriptionCancelAtPeriodEndAsync(
                    sub.StripeSubscriptionId!,
                    cancelAtPeriodEnd: false,
                    cancellationToken);
                if (resumed is null || !resumed.IsSuccess)
                {
                    _logger.LogError(
                        "legacy_reactivate_stripe_failed WorkspaceId={WorkspaceId} StripeSubscription={StripeSubscriptionId} Error={Error}",
                        workspaceId, sub.StripeSubscriptionId, resumed?.Error);
                    return Result.Failure<SubscriptionDto>(
                        string.IsNullOrWhiteSpace(resumed?.Error) ? ApiMessageConstants.ErrorMessages.BillingInternalError : resumed.Error,
                        ErrorCodes.BillingExternalServiceError);
                }

                if (!string.IsNullOrWhiteSpace(resumed.Value))
                {
                    sub.StripeSubscriptionStatus = resumed.Value;
                }
            }
            else if (sub.RenewalMode != SubscriptionConstants.RenewalModes.Invoice)
            {
                // A one-off card purchase (or a pre-#466 plan whose Stripe subscription was never
                // linked): nothing here can charge it again, so "renews" cannot be promised by
                // flipping a flag. That needs a new checkout with auto-renew on.
                return Result.Failure<SubscriptionDto>(
                    StripeSubscriptionLifecycleService.AutoRenewRequiresCheckoutMessage,
                    StripeSubscriptionLifecycleService.AutoRenewRequiresCheckoutCode);
            }

            sub.Reactivate();
            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Entitlements are republished because cancellation published them too. A consumer
            // that lowered a quota on the cancel event has to hear the reversal, or the workspace
            // keeps a downgraded snapshot while its subscription says otherwise.
            await PublishEntitlementsAsync(
                sub.WorkspaceId,
                EntitlementConstants.Reasons.SubscriptionChanged,
                cancellationToken);

            _logger.LogInformation(
                "Subscription reactivated. WorkspaceId={WorkspaceId}, SubscriptionId={SubscriptionId}",
                workspaceId,
                sub.Id);

            var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);
            return Result.Success(plan is null
                ? sub.ToDto(BillingMessageConstants.Subscription.UnknownPlan, 0m)
                : sub.ToDto(plan));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reactivate subscription. WorkspaceId={WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>(
                ApiMessageConstants.ErrorMessages.BillingInternalError,
                ErrorCodes.InternalServerError);
        }
    }

    /// <summary>Returned when a workspace Owner/Admin asks /resume to lift a suspension that is not theirs to lift.</summary>
    public const string ResumeNotAllowedCode = "BILLING_RESUME_NOT_ALLOWED";

    public const string ResumeTrialEndedMessage =
        "The trial has ended. Choose a plan to resume the service.";

    public const string ResumeInvoiceOverdueMessage =
        "An invoice is overdue. Pay it to resume the service.";

    public const string ResumeOtherReasonMessage =
        "This suspension can only be lifted by WarpTalk support.";

    public const string ResumeStillOverCapMessage =
        "The workspace is still over its overage cap. Add credits or raise the cap before resuming.";

    /// <inheritdoc />
    /// <remarks>
    /// WT-878: reason-scoped. This endpoint cleared ANY suspension for a workspace Owner/Admin —
    /// <c>trial_ended</c> and <c>invoice_overdue</c> included — and usage settlement then charged
    /// the workspace until the hourly sweep suspended it again, repeatably. A workspace may now lift
    /// only <c>overage_cap</c>, and only when there is room again (credits left, or overage used
    /// below the effective cap — the rule UpdateContractTermsAsync uses to auto-resume). Every
    /// other reason has its own way out: a plan checkout for a trial, paying the invoice (the
    /// invoice-payment handler lifts it), or support.
    ///
    /// <paramref name="liftAnyReason"/> is for platform staff holding
    /// billing.subscriptions_manage (the controller decides, from the staff resolver): they keep
    /// the unrestricted resume they always had, as do the dedicated admin actions
    /// (AdminWorkspaceBillingService: extend trial, comp period, mark invoice paid).
    /// </remarks>
    public async Task<Result<SubscriptionDto>> ResumeSubscriptionAsync(
        Guid workspaceId,
        ResumeSubscriptionRequest request,
        bool liftAnyReason = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(
                workspaceId, includePlan: false, cancellationToken: cancellationToken);

            if (sub is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            if (sub.ServiceState != SubscriptionConstants.ServiceStates.Suspended)
                return Result.Failure<SubscriptionDto>(
                    BillingMessageConstants.ApiErrorMessages.BillingAiServiceNotSuspended,
                    ErrorCodes.BillingSubscriptionConflict);

            if (!liftAnyReason)
            {
                var refusal = await WorkspaceMayLiftAsync(sub, cancellationToken);
                if (refusal is not null)
                {
                    _logger.LogWarning(
                        "resume_refused WorkspaceId={WorkspaceId} SubscriptionId={SubscriptionId} SuspendedReason={SuspendedReason}",
                        workspaceId, sub.Id, sub.SuspendedReason);
                    return Result.Failure<SubscriptionDto>(refusal, ResumeNotAllowedCode);
                }
            }

            sub.ResumeAiService();
            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await PublishEntitlementsAsync(
                sub.WorkspaceId,
                EntitlementConstants.Reasons.SubscriptionChanged,
                cancellationToken);

            if (_aiServiceStateStore is not null)
            {
                var redisResult = await _aiServiceStateStore.SetAiServiceStateAsync(
                    workspaceId,
                    sub.ServiceState,
                    sub.SuspendedReason,
                    cancellationToken);

                if (!redisResult.IsSuccess)
                    _logger.LogWarning(
                        "Failed to sync resumed AI service state to Redis. WorkspaceId={WorkspaceId}, Error={Error}",
                        workspaceId,
                        redisResult.Error);
            }

            _logger.LogInformation(
                "Billing AI service resumed. WorkspaceId={WorkspaceId}, SubscriptionId={SubscriptionId}, Reason={Reason}",
                workspaceId,
                sub.Id,
                request.Reason);

            var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);
            return Result.Success(plan is null
                ? sub.ToDto(BillingMessageConstants.Subscription.UnknownPlan, 0m)
                : sub.ToDto(plan));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume billing AI service. WorkspaceId={WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>WT-878: null when a workspace Owner/Admin may lift this suspension, otherwise why not.</summary>
    private async Task<string?> WorkspaceMayLiftAsync(Subscription sub, CancellationToken cancellationToken)
    {
        switch (sub.SuspendedReason)
        {
            case SubscriptionConstants.SuspendedReasons.OverageCap:
                break;
            case SubscriptionConstants.SuspendedReasons.TrialEnded:
                return ResumeTrialEndedMessage;
            case SubscriptionConstants.SuspendedReasons.InvoiceOverdue:
                return ResumeInvoiceOverdueMessage;
            default:
                return ResumeOtherReasonMessage;
        }

        var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);
        var effectiveCap = sub.OverageCapCreditsOverride ?? plan?.OverageCapCredits ?? 0;
        return sub.CreditsRemaining > 0 || sub.OverageCreditsThisCycle < effectiveCap
            ? null
            : ResumeStillOverCapMessage;
    }

    /// <summary>What the workspace's billing page shows about running past zero credits.</summary>
    public async Task<Result<WorkspaceOverageSettingDto>> GetOverageSettingAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var (sub, plan, failure) = await LoadActiveSubscriptionAsync<WorkspaceOverageSettingDto>(
            workspaceId, cancellationToken);
        if (failure is not null) return failure;

        var effective = sub!.OverageCapCreditsOverride ?? plan!.OverageCapCredits;
        return Result.Success(new WorkspaceOverageSettingDto(
            Enabled: effective > 0,
            EffectiveCapCredits: effective,
            PlanCapCredits: plan!.OverageCapCredits,
            OverageCreditsThisCycle: sub.OverageCreditsThisCycle));
    }

    /// <summary>
    /// Turn overage on or off for this workspace, WITHIN the allowance its plan already grants.
    ///
    /// Enabling clears the override so the plan's own cap applies; disabling pins it to 0. The
    /// Owner therefore cannot raise their own ceiling — that is what UpdateContractTermsAsync is
    /// for, and why that one is system-admin-only. A plan whose cap is 0 offers no overage at
    /// all, and enabling on it changes nothing, which is reported honestly rather than as success.
    /// </summary>
    public async Task<Result<WorkspaceOverageSettingDto>> SetOverageAsync(
        Guid workspaceId,
        SetWorkspaceOverageRequest request,
        CancellationToken cancellationToken = default)
    {
        var (sub, plan, failure) = await LoadActiveSubscriptionAsync<WorkspaceOverageSettingDto>(
            workspaceId, cancellationToken);
        if (failure is not null) return failure;

        if (request.Enabled && plan!.OverageCapCredits <= 0)
        {
            return Result.Failure<WorkspaceOverageSettingDto>(
                "This plan does not include an overage allowance. Contact WarpTalk to add one.",
                ErrorCodes.ValidationError);
        }

        // null, not the number: the override exists to DIFFER from the plan. Copying the plan's
        // cap into it would freeze today's value, so a later plan change would silently not apply.
        sub!.OverageCapCreditsOverride = request.Enabled ? null : 0;

        // Switching it off must not strand a workspace that is already suspended for having used
        // it — that would make the toggle a one-way door. Switching it ON is the case that can
        // legitimately resume, and only when the room under the cap is real.
        var effective = sub.OverageCapCreditsOverride ?? plan!.OverageCapCredits;
        if (request.Enabled
            && sub.ServiceState == SubscriptionConstants.ServiceStates.Suspended
            && sub.SuspendedReason == SubscriptionConstants.SuspendedReasons.OverageCap
            && sub.OverageCreditsThisCycle < effective)
        {
            sub.ResumeAiService();
        }

        _unitOfWork.SubscriptionRepository.Update(sub);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await PublishEntitlementsAsync(
            sub.WorkspaceId,
            EntitlementConstants.Reasons.ContractOverrideChanged,
            cancellationToken);

        return Result.Success(new WorkspaceOverageSettingDto(
            Enabled: effective > 0,
            EffectiveCapCredits: effective,
            PlanCapCredits: plan!.OverageCapCredits,
            OverageCreditsThisCycle: sub.OverageCreditsThisCycle));
    }

    /// <summary>The active subscription and its plan, or the failure both overage methods return.</summary>
    private async Task<(Subscription? Sub, Plan? Plan, Result<T>? Failure)> LoadActiveSubscriptionAsync<T>(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // WT-430: deliberately BROADER than Subscription.GrantsPlanEntitlements, and left that way.
        // This finds the subscription to bill or credit, not the one that grants plan quotas — a
        // cancelled subscription still inside its paid period keeps its credits until the period
        // ends, so narrowing this to the entitlement test would take money handling with it.
        var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(
            workspaceId, includePlan: false, cancellationToken: cancellationToken);

        if (sub is null)
            return (null, null, Result.Failure<T>(
                ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                ErrorCodes.BillingSubscriptionNotFound));

        var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);
        if (plan is null)
            return (null, null, Result.Failure<T>(
                ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                ErrorCodes.BillingPlanNotFound));

        return (sub, plan, null);
    }

    public async Task<Result<SubscriptionDto>> AdminChangePlanAsync(
        Guid workspaceId,
        Guid planId,
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(
                workspaceId, includePlan: false, cancellationToken: cancellationToken);

            if (sub is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            if (sub.PlanId == planId)
                return Result.Failure<SubscriptionDto>(
                    "The subscription is already on this plan.",
                    ErrorCodes.BillingSubscriptionConflict);

            var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(
                p => p.Id == planId && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            // A hidden plan is retired from sale; moving a customer ONTO one recreates it by the
            // back door and puts numbers in force that no price page describes.
            if (!plan.IsActive)
                return Result.Failure<SubscriptionDto>(
                    "The target plan is deactivated. Reactivate it before moving a subscription onto it.",
                    ErrorCodes.ValidationError);

            sub.PlanId = plan.Id;
            sub.UpdatedAt = DateTime.UtcNow;
            sub.UpdatedBy = adminUserId;
            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Layer 2 of the resolution order just moved for this workspace.
            await PublishEntitlementsAsync(
                sub.WorkspaceId,
                EntitlementConstants.Reasons.PlanChanged,
                cancellationToken);

            _logger.LogInformation(
                "System admin {AdminUserId} moved workspace {WorkspaceId} subscription {SubscriptionId} to plan {PlanId} ({PlanName})",
                adminUserId, workspaceId, sub.Id, plan.Id, plan.Name);

            return Result.Success(sub.ToDto(plan));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing plan for workspace {WorkspaceId} to plan {PlanId}", workspaceId, planId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<SubscriptionDto>> UpdateContractTermsAsync(
        Guid workspaceId,
        UpdateSubscriptionContractTermsRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(
                workspaceId, includePlan: false, cancellationToken: cancellationToken);

            if (sub is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            var plan = await _unitOfWork.Plans.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            var pricingConfig = await GetPricingConfigAsync(cancellationToken);
            var validation = ValidateContractTerms(sub, plan, request, pricingConfig);
            if (!validation.IsSuccess)
                return Result.Failure<SubscriptionDto>(
                    validation.Error ?? BillingMessageConstants.ApiErrorMessages.BillingContractTermsInvalid,
                    validation.ErrorCode);

            bool wasSuspendedForOverage = sub.ServiceState == SubscriptionConstants.ServiceStates.Suspended &&
                                          sub.SuspendedReason == SubscriptionConstants.SuspendedReasons.OverageCap;

            sub.ApplyContractTerms(request);

            bool isResumed = false;
            if (wasSuspendedForOverage)
            {
                var currentOverageCap = sub.OverageCapCreditsOverride ?? plan.OverageCapCredits;
                if (sub.CreditsRemaining > 0 || sub.OverageCreditsThisCycle < currentOverageCap)
                {
                    sub.ResumeAiService();
                    isResumed = true;
                }
            }

            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            // Contract terms are layer 3 of the resolution order, so a change here can move an
            // entitlement even when the plan and the workspace's own settings are untouched.
            await PublishEntitlementsAsync(
                sub.WorkspaceId,
                EntitlementConstants.Reasons.ContractOverrideChanged,
                cancellationToken);

            if (isResumed && _aiServiceStateStore is not null)
            {
                var redisResult = await _aiServiceStateStore.SetAiServiceStateAsync(
                    workspaceId,
                    sub.ServiceState,
                    sub.SuspendedReason,
                    cancellationToken);

                if (!redisResult.IsSuccess)
                    _logger.LogWarning("Failed to push auto-resumed AI state to Redis for WorkspaceId {WorkspaceId}", workspaceId);
            }

            await BillingNotificationHelper.PublishSubscriptionUpdateAsync(
                _messagePublisher,
                _logger,
                sub.UserId,
                BillingMessageConstants.Notifications.ActionChanged,
                plan.Name,
                cancellationToken);

            return Result.Success(sub.ToDto(plan));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating contract terms for WorkspaceId {WorkspaceId}", workspaceId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    private static Result ValidateContractTerms(
        Subscription subscription,
        Plan plan,
        UpdateSubscriptionContractTermsRequest request,
        PricingConfigDto? pricingConfig)
    {
        if (request.CreditsPerCycleOverride is <= 0 ||
            request.ContractPriceVnd is < 0 ||
            request.OverageCapCreditsOverride is < 0 ||
            request.OveragePricePerCreditOverride is < 0 ||
            request.InvoiceTermsDaysOverride is <= 0)
        {
            return Result.Failure(
                BillingMessageConstants.ApiErrorMessages.BillingContractTermsInvalid,
                ErrorCodes.ValidationError);
        }

        var currentCreditsPerCycle = subscription.CreditsPerCycleOverride ?? plan.CreditsPerCycle;
        var currentOverageCap = subscription.OverageCapCreditsOverride ?? plan.OverageCapCredits;
        var nextCreditsPerCycle = request.CreditsPerCycleOverride ?? plan.CreditsPerCycle;
        var nextContractPrice = request.ContractPriceVnd ?? plan.Price;
        var nextOverageCap = request.OverageCapCreditsOverride ?? plan.OverageCapCredits;
        var nextOveragePrice = request.OveragePricePerCreditOverride ?? plan.OveragePricePerCredit;
        var minimumPricePerCreditVnd = pricingConfig?.MinimumPricePerCreditVnd ?? SubscriptionConstants.PlanDefaults.PriceFloorPerCredit;

        if (!string.IsNullOrWhiteSpace(request.BillingContactEmail) && !IsValidEmail(request.BillingContactEmail))
        {
            return Result.Failure(
                BillingMessageConstants.ApiErrorMessages.BillingContractTermsInvalid,
                ErrorCodes.ValidationError);
        }

        if (nextCreditsPerCycle > 0 &&
            nextContractPrice / nextCreditsPerCycle < minimumPricePerCreditVnd)
        {
            return Result.Failure(
                BillingMessageConstants.ApiErrorMessages.BillingContractPriceBelowFloor,
                ErrorCodes.ValidationError);
        }

        if (nextOverageCap > nextCreditsPerCycle ||
            (nextOverageCap > 0 && nextOveragePrice < plan.OveragePricePerCredit))
        {
            return Result.Failure(
                BillingMessageConstants.ApiErrorMessages.BillingContractOverageTermsInvalid,
                ErrorCodes.ValidationError);
        }

        if (subscription.OverageStartedAt is not null &&
            (nextCreditsPerCycle < currentCreditsPerCycle ||
             nextOverageCap < currentOverageCap ||
             nextOverageCap < subscription.OverageCreditsThisCycle))
        {
            return Result.Failure(
                BillingMessageConstants.ApiErrorMessages.BillingCannotReduceCommitmentDuringOverage,
                ErrorCodes.BillingSubscriptionConflict);
        }

        return Result.Success();
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var address = new MailAddress(email.Trim());
            return string.Equals(address.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<PricingConfigDto?> GetPricingConfigAsync(CancellationToken cancellationToken)
    {
        var result = await _pricingConfigService.GetPricingConfigAsync(cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }

}
