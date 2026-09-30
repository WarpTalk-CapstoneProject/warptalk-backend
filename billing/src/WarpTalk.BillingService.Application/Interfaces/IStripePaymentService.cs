using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IStripePaymentService
{
    Task<Result<string>> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request, CancellationToken cancellationToken = default);
    Task<Result<bool>> CancelSubscriptionAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<(string Status, string FailureReason)>> GetPaymentStatusAsync(string providerTransactionId, CancellationToken cancellationToken = default);
    Task<Result<CheckoutSessionDto>> GetCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// G11: a checkout for a catalog item (credit pack, add-on, or a plan with a coupon). The line,
    /// discount and metadata in <paramref name="extras"/> were built server-side by
    /// ICustomerCatalogService; nothing in them came from the request body.
    /// </summary>
    Task<Result<string>> CreateCatalogCheckoutSessionAsync(
        CreateCheckoutSessionRequest request,
        CheckoutExtras extras,
        CancellationToken cancellationToken = default);

    /// <summary>G11: cancels ONE Stripe subscription (an add-on's) at the end of its paid period.</summary>
    Task<Result<DateTime?>> CancelStripeSubscriptionAtPeriodEndAsync(
        string stripeSubscriptionId,
        CancellationToken cancellationToken = default);
}
