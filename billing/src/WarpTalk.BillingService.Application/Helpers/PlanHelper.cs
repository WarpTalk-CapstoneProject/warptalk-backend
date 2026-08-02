using System.Linq;
using System.Text.RegularExpressions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Helpers;

public static class PlanHelper
{
    public static Result<PlanDto> ValidatePlanRequest(PlanRequest request)
        => ValidatePlanRequest(request, null);

    public static Result<PlanDto> ValidatePlanRequest(PlanRequest request, PricingConfigDto? pricingConfig)
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
            (request.MaxLanguages < 1 || request.MaxLanguages > SubscriptionConstants.FeatureAccess.DefaultMaxLanguages, ApiMessageConstants.ValidationMessages.PlanMaxLanguagesInvalid),
            (request.SortOrder < 0, ApiMessageConstants.ValidationMessages.PlanSortOrderInvalid),
            (isInvalidFeatures, ApiMessageConstants.ValidationMessages.PlanFeaturesInvalid)
        };

        var error = validations.FirstOrDefault(v => v.IsInvalid);
        if (error.IsInvalid)
            return Result.Failure<PlanDto>(error.ErrorMessage, ErrorCodes.ValidationError);

        return Result.Success<PlanDto>(null!);
    }
}
