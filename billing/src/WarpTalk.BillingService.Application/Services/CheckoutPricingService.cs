using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public sealed class CheckoutPricingService : ICheckoutPricingService
{
    private const decimal MinimumTopUpVnd = 15_000m;
    private const decimal MaximumTopUpVnd = 10_000_000m;
    private readonly IPlanService _planService;

    public CheckoutPricingService(IPlanService planService)
    {
        _planService = planService;
    }

    public async Task<Result<ResolvedCheckout>> ResolveAsync(
        CreateCheckoutSessionRequest request,
        Guid authenticatedUserId,
        CancellationToken cancellationToken = default)
    {
        if (request.UserId != Guid.Empty && request.UserId != authenticatedUserId)
            return Result.Failure<ResolvedCheckout>(
                "The checkout user does not match the authenticated user.",
                ErrorCodes.Forbidden);

        if (request.WorkspaceId == Guid.Empty)
            return Result.Failure<ResolvedCheckout>(
                "WorkspaceId is required.",
                ErrorCodes.ValidationError);

        if (request.PaymentType.Equals("Subscription", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.PlanSlug))
                return Result.Failure<ResolvedCheckout>(
                    "PlanSlug is required for a subscription checkout.",
                    ErrorCodes.ValidationError);

            var planResult = await _planService.GetPlanBySlugAsync(
                request.PlanSlug.Trim(),
                cancellationToken);

            if (!planResult.IsSuccess || planResult.Value is null || !planResult.Value.IsActive)
                return Result.Failure<ResolvedCheckout>(
                    planResult.Error ?? "Subscription plan was not found.",
                    ErrorCodes.BillingPlanNotFound);

            var plan = planResult.Value;
            if (plan.Price <= 0)
                return Result.Failure<ResolvedCheckout>(
                    "The selected subscription plan is not billable.",
                    ErrorCodes.ValidationError);

            return Result.Success(new ResolvedCheckout(
                authenticatedUserId,
                request.WorkspaceId,
                plan.Price,
                plan.Currency.ToLowerInvariant(),
                "Subscription",
                plan.Slug,
                plan.BillingCycle,
                plan.Name));
        }

        if (!request.PaymentType.Equals("CreditTopUp", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<ResolvedCheckout>(
                "PaymentType must be Subscription or CreditTopUp.",
                ErrorCodes.ValidationError);

        if (!request.Currency.Equals("VND", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<ResolvedCheckout>(
                "Credit top-ups currently support VND only.",
                ErrorCodes.ValidationError);

        if (request.Amount < MinimumTopUpVnd || request.Amount > MaximumTopUpVnd)
            return Result.Failure<ResolvedCheckout>(
                $"Credit top-up amount must be between {MinimumTopUpVnd:0} and {MaximumTopUpVnd:0} VND.",
                ErrorCodes.ValidationError);

        return Result.Success(new ResolvedCheckout(
            authenticatedUserId,
            request.WorkspaceId,
            request.Amount,
            "vnd",
            "CreditTopUp",
            string.Empty,
            string.Empty,
            "WarpTalk credit top-up"));
    }
}
