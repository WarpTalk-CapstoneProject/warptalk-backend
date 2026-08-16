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

    public SubscriptionService(
        IUnitOfWork unitOfWork,
        ILogger<SubscriptionService> logger,
        IBillingMessagePublisher messagePublisher,
        IStripePaymentService stripePaymentService,
        IUsageRateCardAdminService pricingConfigService,
        IWorkspaceClient workspaceClient,
        IAiServiceStateStore? aiServiceStateStore = null,
        IEntitlementChangePublisher? entitlementChangePublisher = null)
    {
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
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

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

            var subscription = request.ToTrialEntity(plan, ownerDomain);

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
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<bool>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            if (sub.TrialEndsAt != null)
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

            // Call Stripe service to cancel Stripe Subscription
            try
            {
                var cancelResult = await _stripePaymentService.CancelSubscriptionAsync(workspaceId, cancellationToken);
                if (!cancelResult.IsSuccess)
                    _logger.LogWarning(BillingMessageConstants.LogMessages.ErrorCancellingStripeSubscription, workspaceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, BillingMessageConstants.LogMessages.ErrorCancellingStripeSubscription, workspaceId);
            }

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

    public async Task<Result<SubscriptionDto>> ResumeSubscriptionAsync(
        Guid workspaceId,
        ResumeSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            if (sub.ServiceState != SubscriptionConstants.ServiceStates.Suspended)
                return Result.Failure<SubscriptionDto>(
                    BillingMessageConstants.ApiErrorMessages.BillingAiServiceNotSuspended,
                    ErrorCodes.BillingSubscriptionConflict);

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
        var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
            cancellationToken);

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

    public async Task<Result<SubscriptionDto>> UpdateContractTermsAsync(
        Guid workspaceId,
        UpdateSubscriptionContractTermsRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

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
