using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
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

    public PlanService(
        IUnitOfWork unitOfWork,
        ILogger<PlanService> logger,
        IBillingMessagePublisher messagePublisher,
        IUsageRateCardAdminService pricingConfigService)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
        _pricingConfigService = pricingConfigService;
    }

    public async Task<Result<IEnumerable<PlanDto>>> GetActivePlansAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plans = (await _unitOfWork.Plans.FindAsync(
                p => p.DeletedAt == null,
                cancellationToken)).ToList();

            if (!plans.Any())
            {
                var defaultEnterprisePlan = PlanMapper.CreateDefaultEnterprisePlan();
                
                await _unitOfWork.Plans.AddAsync(defaultEnterprisePlan, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                
                plans.Add(defaultEnterprisePlan);
            }

            return Result.Success(plans.Select(p => p.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingPlans);
            return Result.Failure<IEnumerable<PlanDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
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
