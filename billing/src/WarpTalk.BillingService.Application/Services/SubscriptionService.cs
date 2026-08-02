using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
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
    private readonly IWorkspaceClient _workspaceClient;
    private readonly IAiServiceStateStore? _aiServiceStateStore;
    private readonly IUsageRateCardAdminService _pricingConfigService;

    public SubscriptionService(
        IUnitOfWork unitOfWork,
        ILogger<SubscriptionService> logger,
        IBillingMessagePublisher messagePublisher,
        IStripePaymentService stripePaymentService,
        IWorkspaceClient workspaceClient,
        IUsageRateCardAdminService pricingConfigService,
        IAiServiceStateStore? aiServiceStateStore = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _stripePaymentService = stripePaymentService;
        _workspaceClient = workspaceClient;
        _pricingConfigService = pricingConfigService;
        _aiServiceStateStore = aiServiceStateStore;
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

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
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
                var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
                items.Add(plan is null
                    ? sub.ToDto(BillingMessageConstants.PlanAuditMessages.UnknownPlan, 0m)
                    : sub.ToDto(plan));
            }

            // Resolve workspace names cross-schema
            try
            {
                var workspaceIds = BillingQueryHelper.GetWorkspaceIds(items);

                if (workspaceIds.Length > 0)
                {
                    var workspaceNames = await _unitOfWork.CreditTransactionRepository.GetWorkspaceNamesAsync(workspaceIds, cancellationToken);
                    items = BillingQueryHelper.ApplyWorkspaceNames(items, workspaceNames);
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

    public async Task<Result<SubscriptionDto>> CreateSubscriptionAsync(
        SubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
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

            var subscription = request.ToEntity(plan);

            await _unitOfWork.SubscriptionRepository.AddAsync(subscription, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

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
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorCreatingSubscription, request.WorkspaceId, request.PlanId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<SubscriptionDto>> CreateWorkspaceContractSubscriptionAsync(
        CreateWorkspaceContractSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
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

            var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
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

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);

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

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
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

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
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

            sub.ApplyContractTerms(request);
            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

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

    public async Task<Result<SubscriptionDto>> ChangeSubscriptionAsync(
        SubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var oldSub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == request.WorkspaceId && s.IsActive && s.DeletedAt == null,
                cancellationToken);

            if (oldSub is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            if (oldSub.PlanId == request.PlanId)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionAlreadyActive,
                    ErrorCodes.BillingSubscriptionAlreadyActive);

            var newPlan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Id == request.PlanId && p.IsActive && p.DeletedAt == null,
                cancellationToken);

            if (newPlan is null)
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            // Try to update the Stripe subscription directly with proration
            bool stripeUpdated = false;
            try
            {
                var updateResult = await _stripePaymentService.UpdateSubscriptionAsync(
                    new UpdateStripeSubscriptionRequest(request.WorkspaceId, newPlan.Price, newPlan.Currency, newPlan.Slug),
                    cancellationToken);
                if (updateResult.IsSuccess)
                    stripeUpdated = updateResult.Value;
                else
                    _logger.LogWarning(BillingMessageConstants.LogMessages.FailedToUpdateStripeSubChangePlan, request.WorkspaceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToUpdateStripeSubChangePlan, request.WorkspaceId);
            }

            if (!stripeUpdated)
            {
                return Result.Failure<SubscriptionDto>(
                    ApiMessageConstants.ErrorMessages.BillingStripeUpdateFailed,
                    ErrorCodes.InternalServerError);
            }

            // With Webhook architecture, we do not update local DB synchronously.
            // The local DB will be updated when Stripe sends the customer.subscription.updated webhook.
            // We return a "Pending" DTO to the client so the UI can show a loading state.
            var pendingSub = request.ToEntity(oldSub, newPlan);
            pendingSub.Status = SubscriptionConstants.SubscriptionStatuses.Pending;

            return Result.Success(pendingSub.ToDto(newPlan));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorChangingSubscription, request.WorkspaceId, request.PlanId);
            return Result.Failure<SubscriptionDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<bool>> ActivateSubscriptionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
                s => s.WorkspaceId == workspaceId && s.Status == SubscriptionConstants.SubscriptionStatuses.Pending && s.DeletedAt == null,
                cancellationToken);

            if (sub is null)
                return Result.Failure<bool>(
                    ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                    ErrorCodes.BillingSubscriptionNotFound);

            var plan = await _unitOfWork.PlanRepository.GetByIdAsync(sub.PlanId, cancellationToken);
            if (plan is null)
                return Result.Failure<bool>(
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            sub.IsActive = true;
            sub.Status = SubscriptionConstants.SubscriptionStatuses.Active;
            sub.ApplyCycleAllocation(plan.CreditsPerCycle);
            sub.CurrentPeriodEnd = CalculatePeriodEnd(DateTime.UtcNow, plan.BillingCycle);
            sub.UpdatedAt = DateTime.UtcNow;

            _unitOfWork.SubscriptionRepository.Update(sub);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await BillingNotificationHelper.PublishSubscriptionUpdateAsync(
                _messagePublisher,
                _logger,
                sub.UserId,
                BillingMessageConstants.Subscription.Actions.Activated,
                plan.Name,
                cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorActivatingSubscription, workspaceId);
            return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    private static DateTime CalculatePeriodEnd(DateTime start, string billingCycle)
    {
        return start.AddMonths(1);
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
