using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ICheckoutPricingService
{
    Task<Result<ResolvedCheckout>> ResolveAsync(
        CreateCheckoutSessionRequest request,
        Guid authenticatedUserId,
        CancellationToken cancellationToken = default);
}
