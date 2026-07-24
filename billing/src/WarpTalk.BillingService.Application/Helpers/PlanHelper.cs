using System.Linq;
using System.Text.RegularExpressions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Helpers;

public static class PlanHelper
{
    public static Result<PlanDto> ValidatePlanRequest(PlanRequest request)
    {
        var currency = request.Currency?.Trim() ?? "";
        const decimal minPrice = 0.50m;

        var cycle = request.BillingCycle?.ToLowerInvariant().Trim();
        var isInvalidCycle = cycle is not (SubscriptionConstants.BillingCycles.Monthly or
                                          SubscriptionConstants.BillingCycles.Semiannual or
                                          SubscriptionConstants.BillingCycles.Yearly);
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
            (!string.Equals(currency, PaymentConstants.Currencies.Usd, System.StringComparison.OrdinalIgnoreCase), ApiMessageConstants.ValidationMessages.PlanCurrencyInvalid),
            (string.IsNullOrWhiteSpace(request.BillingCycle), ApiMessageConstants.ValidationMessages.PlanBillingCycleRequired),
            (!string.IsNullOrWhiteSpace(request.BillingCycle) && isInvalidCycle, ApiMessageConstants.ValidationMessages.PlanBillingCycleInvalid),
            (request.Price < minPrice, string.Format(ApiMessageConstants.ValidationMessages.PlanMinPrice, currency, minPrice)),
            (request.CreditsPerCycle < 0, ApiMessageConstants.ValidationMessages.PlanCreditsPerCycleInvalid),
            (request.MaxParticipants < 2, ApiMessageConstants.ValidationMessages.PlanMaxParticipantsInvalid),
            (request.SortOrder < 0, ApiMessageConstants.ValidationMessages.PlanSortOrderInvalid),
            (isInvalidFeatures, ApiMessageConstants.ValidationMessages.PlanFeaturesInvalid)
        };

        var error = validations.FirstOrDefault(v => v.IsInvalid);
        if (error.IsInvalid)
            return Result.Failure<PlanDto>(error.ErrorMessage, ErrorCodes.ValidationError);

        return Result.Success<PlanDto>(null!);
    }
}
