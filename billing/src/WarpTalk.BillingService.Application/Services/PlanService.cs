using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Entitlements;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Helpers;
using System.Text.RegularExpressions;

using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class PlanService : IPlanService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PlanService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;
    private readonly IUsageRateCardAdminService _pricingConfigService;
    private readonly IEntitlementChangePublisher? _entitlementChangePublisher;

    public PlanService(
        IUnitOfWork unitOfWork,
        ILogger<PlanService> logger,
        IBillingMessagePublisher messagePublisher,
        IUsageRateCardAdminService pricingConfigService,
        IEntitlementChangePublisher? entitlementChangePublisher = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _pricingConfigService = pricingConfigService;
        _entitlementChangePublisher = entitlementChangePublisher;
    }

    /// <summary>
    /// WT-263: a plan edit moves layer 2 of the resolution order for EVERY workspace on that plan,
    /// so each of them needs a fresh snapshot — this is the fan-out case the push architecture has
    /// to handle, and the reason enforcement can read a local table at all.
    ///
    /// Fanned out one workspace at a time through the outbox rather than as a single broadcast
    /// "plan X changed" event: a consumer receiving a plan-level event would have to know which of
    /// its workspaces are on that plan and what the plan means, which is re-deriving entitlements in
    /// the consumer — the exact thing this ticket removes.
    /// </summary>
    private async Task PublishEntitlementsForPlanAsync(Guid planId, CancellationToken ct)
    {
        if (_entitlementChangePublisher is null)
        {
            return;
        }

        try
        {
            var affected = await _unitOfWork.SubscriptionRepository.FindAsync(
                subscription => subscription.PlanId == planId && subscription.DeletedAt == null,
                ct);

            foreach (var workspaceId in affected.Select(subscription => subscription.WorkspaceId).Distinct())
            {
                await _entitlementChangePublisher.EnqueueAsync(
                    workspaceId,
                    EntitlementConstants.Reasons.PlanChanged,
                    ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception exception)
        {
            // Never fails the plan edit itself; the admin's change is already committed.
            _logger.LogError(exception, "Failed to enqueue entitlement changes for plan {PlanId}.", planId);
        }
    }

    /// <summary>
    /// BR-74 — the customer-facing catalogue. Only plans that are actually on sale.
    ///
    /// This filtered on DeletedAt alone despite its name, so a plan an administrator had
    /// deactivated stayed selectable for new purchases on the landing page and in every checkout
    /// flow. `SubscriptionService` already refuses to create a subscription against an inactive
    /// plan, so the end state was a customer picking a plan and being told no at the till.
    ///
    /// Administrators must NOT use this. Deactivating a plan through the edit form would remove it
    /// from the only list the admin page has, and there would be no way to switch it back on —
    /// deactivation would be a one-way door. That is what GetAllPlansAsync below is for.
    /// </summary>
    public async Task<Result<IEnumerable<PlanDto>>> GetActivePlansAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plans = await LoadCatalogueAsync(cancellationToken);
            return Result.Success(plans.Where(p => p.IsActive).Select(p => p.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingPlans);
            return Result.Failure<IEnumerable<PlanDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Every plan, deactivated ones included. System Admin only — see the controller.
    /// </summary>
    public async Task<Result<IEnumerable<PlanDto>>> GetAllPlansAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plans = await LoadCatalogueAsync(cancellationToken);
            return Result.Success(plans.Select(p => p.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingPlans);
            return Result.Failure<IEnumerable<PlanDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Every non-deleted plan, seeding the default Enterprise plan on a genuinely empty catalogue.
    ///
    /// The seed is keyed on "no plans exist", NOT on "no ACTIVE plans exist". Those differ exactly
    /// when an administrator has deactivated everything — and seeding there would mint a brand new
    /// Enterprise plan every time the catalogue was read, silently undoing the decision to take the
    /// product off sale.
    /// </summary>
    private async Task<List<Plan>> LoadCatalogueAsync(CancellationToken cancellationToken)
    {
        var plans = (await _unitOfWork.Plans.FindAsync(
            p => p.DeletedAt == null,
            cancellationToken)).ToList();

        if (plans.Count == 0)
        {
            var defaultEnterprisePlan = PlanMapper.CreateDefaultEnterprisePlan();
            await _unitOfWork.Plans.AddAsync(defaultEnterprisePlan, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            plans.Add(defaultEnterprisePlan);
        }

        return plans;
    }

    public async Task<Result<PlanDto>> GetPlanByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(
                p => p.Id == id && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<PlanDto>(
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingPlanById, id);
            return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }


    public async Task<Result<PlanDto>> UpdatePlanAsync(
        Guid id, PlanRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.Plans.FirstOrDefaultAsync(
                p => p.Id == id && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);

            var pricingConfig = await GetPricingConfigAsync(cancellationToken);
            var validationResult = ValidatePlanRequest(request, pricingConfig);
            if (!validationResult.IsSuccess)
                return validationResult;

            var normalizedSlug = request.Slug.ToLowerInvariant().Trim();
            if (plan.Slug != normalizedSlug)
            {
                var existing = await _unitOfWork.Plans.FirstOrDefaultAsync(
                    p => p.Slug == normalizedSlug && p.Id != id && p.DeletedAt == null,
                    cancellationToken);

                if (existing is not null)
                    return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingDuplicatePlanSlug, ErrorCodes.BillingDuplicatePlanSlug);
            }

            var changes = new List<string>();

            if (plan.Price != request.Price)
                changes.Add(string.Format(BillingMessageConstants.PlanAuditMessages.PriceChanged, plan.Price, request.Price, plan.Currency));

            AuditHelper.Track(changes, plan.CreditsPerCycle, request.CreditsPerCycle, BillingMessageConstants.PlanAuditMessages.CreditsChanged);
            AuditHelper.Track(changes, plan.MaxParticipants, request.MaxParticipants, BillingMessageConstants.PlanAuditMessages.MaxParticipantsChanged);
            AuditHelper.Track(changes, plan.Name, request.Name, BillingMessageConstants.PlanAuditMessages.NameChanged);

            string? changeDetail = changes.Any() ? string.Join("; ", changes) : null;

            plan.UpdateFromRequest(request);
            _unitOfWork.Plans.Update(plan);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await PublishEntitlementsForPlanAsync(plan.Id, cancellationToken);

            await BillingNotificationHelper.PublishPlanUpdateAsync(
                _messagePublisher,
                _logger,
                BillingMessageConstants.Plan.Actions.Updated,
                plan.Name,
                changeDetail,
                cancellationToken);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorUpdatingPlan);
            return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }


    public async Task<Result<PlanDto>> CreatePlanAsync(
        PlanRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var pricingConfig = await GetPricingConfigAsync(cancellationToken);
            var validationResult = ValidatePlanRequest(request, pricingConfig);
            if (!validationResult.IsSuccess)
                return validationResult;

            var normalizedSlug = request.Slug.ToLowerInvariant().Trim();
            var existing = await _unitOfWork.Plans.FirstOrDefaultAsync(
                p => p.Slug == normalizedSlug && p.DeletedAt == null,
                cancellationToken);
            if (existing is not null)
                return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingDuplicatePlanSlug, ErrorCodes.BillingDuplicatePlanSlug);

            // ToEntity was written alongside UpdateFromRequest and then sat unwired — the
            // catalogue only ever grew by migration. This is its first caller.
            var plan = request.ToEntity();
            await _unitOfWork.Plans.AddAsync(plan, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // No entitlement fan-out: a plan nobody subscribes to yet moves nobody's layer 2.
            await BillingNotificationHelper.PublishPlanUpdateAsync(
                _messagePublisher,
                _logger,
                BillingMessageConstants.Plan.Actions.Created,
                plan.Name,
                null,
                cancellationToken);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorUpdatingPlan);
            return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    private async Task<PricingConfigDto?> GetPricingConfigAsync(CancellationToken cancellationToken)
    {
        var result = await _pricingConfigService.GetPricingConfigAsync(cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }

    private static Result<PlanDto> ValidatePlanRequest(PlanRequest request, PricingConfigDto? pricingConfig = null)
    {
        var currency = request.Currency?.Trim() ?? "";
        var cycle = request.BillingCycle?.ToLowerInvariant().Trim();
        var isInvalidCurrency = !string.Equals(currency, PaymentConstants.Currencies.Usd, System.StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(currency, PaymentConstants.Currencies.Vnd, System.StringComparison.OrdinalIgnoreCase);
        var minPrice = string.Equals(currency, PaymentConstants.Currencies.Vnd, System.StringComparison.OrdinalIgnoreCase)
            ? pricingConfig?.MinimumContractPriceVnd ?? SubscriptionConstants.PlanDefaults.MinimumVndPlanPrice
            : pricingConfig?.MinimumContractPriceUsd ?? SubscriptionConstants.PlanDefaults.MinimumUsdPlanPrice;
        var minimumPricePerCreditVnd = pricingConfig?.MinimumPricePerCreditVnd ?? SubscriptionConstants.PlanDefaults.PriceFloorPerCredit;
        var hasCommittedCredits = request.CreditsPerCycle > 0;
        var isVndPlan = string.Equals(currency, PaymentConstants.Currencies.Vnd, System.StringComparison.OrdinalIgnoreCase);
        var isBelowPriceFloor = isVndPlan &&
                                hasCommittedCredits &&
                                request.Price / request.CreditsPerCycle < minimumPricePerCreditVnd;
        var isOverageCapAboveCommitment = hasCommittedCredits &&
                                          request.OverageCapCredits > request.CreditsPerCycle;
        var isLowBalanceAtOrAboveCommitment = hasCommittedCredits &&
                                              request.LowBalanceThresholdCredits >= request.CreditsPerCycle;
        var isRolloverCapAboveCommitment = hasCommittedCredits &&
                                           request.RolloverCapCredits > request.CreditsPerCycle;
        var shouldWarnBeforeOverage = request.OverageCapCredits > 0 &&
                                      request.LowBalanceThresholdCredits <= request.OverageCapCredits;
        var isMissingOveragePrice = request.OverageCapCredits > 0 &&
                                    request.OveragePricePerCredit <= 0;
        var isInvalidCycle = cycle is not SubscriptionConstants.BillingCycles.Monthly;
        var isInvalidFeatures = !string.IsNullOrWhiteSpace(request.Features) &&
                                !(request.Features.Trim().StartsWith("{") && request.Features.Trim().EndsWith("}")) &&
                                !(request.Features.Trim().StartsWith("[") && request.Features.Trim().EndsWith("]"));

        var validations = new (bool IsInvalid, string ErrorMessage)[]
        {
            (string.IsNullOrWhiteSpace(request.Name), ApiMessageConstants.ValidationMessages.PlanNameRequired),
            (request.Name?.Length > 100, ApiMessageConstants.ValidationMessages.PlanNameMaxLength),
            (string.IsNullOrWhiteSpace(request.Slug), ApiMessageConstants.ValidationMessages.PlanSlugRequired),
            (request.Slug?.Length > 50, ApiMessageConstants.ValidationMessages.PlanSlugMaxLength),
            (!string.IsNullOrWhiteSpace(request.Slug) && !Regex.IsMatch(request.Slug, BillingMessageConstants.Validation.Plan.SlugPattern), ApiMessageConstants.ValidationMessages.PlanSlugInvalid),
            (string.IsNullOrWhiteSpace(request.Tier), ApiMessageConstants.ValidationMessages.PlanTierRequired),
            (request.Tier?.Length > 20, ApiMessageConstants.ValidationMessages.PlanTierMaxLength),
            (string.IsNullOrWhiteSpace(request.Currency), ApiMessageConstants.ValidationMessages.PlanCurrencyRequired),
            (isInvalidCurrency, ApiMessageConstants.ValidationMessages.PlanCurrencyInvalid),
            (string.IsNullOrWhiteSpace(request.BillingCycle), ApiMessageConstants.ValidationMessages.PlanBillingCycleRequired),
            (!string.IsNullOrWhiteSpace(request.BillingCycle) && isInvalidCycle, ApiMessageConstants.ValidationMessages.PlanBillingCycleInvalid),
            (request.Price < minPrice, string.Format(ApiMessageConstants.ValidationMessages.PlanMinPrice, currency, minPrice)),
            (request.CreditsPerCycle <= 0, ApiMessageConstants.ValidationMessages.PlanCreditsPerCycleInvalid),
            (isBelowPriceFloor, ApiMessageConstants.ValidationMessages.PlanEffectivePriceFloorInvalid),
            (request.OverageCapCredits < 0, ApiMessageConstants.ValidationMessages.PlanOverageCapInvalid),
            (isOverageCapAboveCommitment, ApiMessageConstants.ValidationMessages.PlanOverageCapTooHigh),
            (request.OveragePricePerCredit < 0, ApiMessageConstants.ValidationMessages.PlanOveragePriceInvalid),
            (isMissingOveragePrice, ApiMessageConstants.ValidationMessages.PlanOveragePriceRequired),
            (shouldWarnBeforeOverage, ApiMessageConstants.ValidationMessages.PlanLowBalanceThresholdInvalid),
            (isLowBalanceAtOrAboveCommitment, ApiMessageConstants.ValidationMessages.PlanLowBalanceThresholdTooHigh),
            (request.RolloverCapCredits < 0, ApiMessageConstants.ValidationMessages.PlanRolloverCapInvalid),
            (isRolloverCapAboveCommitment, ApiMessageConstants.ValidationMessages.PlanRolloverCapTooHigh),
            (request.InvoiceTermsDays <= 0, ApiMessageConstants.ValidationMessages.PlanInvoiceTermsInvalid),
            (request.InvoiceGraceHours <= 0, ApiMessageConstants.ValidationMessages.PlanInvoiceGraceInvalid),
            (request.MaxParticipants < 2, ApiMessageConstants.ValidationMessages.PlanMaxParticipantsInvalid),
            (request.MaxLanguages < 1 || request.MaxLanguages > SubscriptionConstants.PlanDefaults.MaxLanguagesCeiling, ApiMessageConstants.ValidationMessages.PlanMaxLanguagesInvalid),
            (request.SortOrder < 0, ApiMessageConstants.ValidationMessages.PlanSortOrderInvalid),
            (isInvalidFeatures, ApiMessageConstants.ValidationMessages.PlanFeaturesInvalid)
        };

        var error = validations.FirstOrDefault(v => v.IsInvalid);
        if (error.IsInvalid)
            return Result.Failure<PlanDto>(error.ErrorMessage, ErrorCodes.ValidationError);

        return Result.Success<PlanDto>(null!);
    }

}
