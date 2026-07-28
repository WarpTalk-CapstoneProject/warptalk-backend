using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.API.Services;

public interface IStripeBillingGateway
{
    Task<string> CreateCheckoutAsync(
        ResolvedCheckout checkout,
        CancellationToken cancellationToken = default);

    Task<CheckoutSessionDto?> GetCheckoutSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    ProcessPaymentEventRequest? ParseWebhook(string payload, string signature);
}
