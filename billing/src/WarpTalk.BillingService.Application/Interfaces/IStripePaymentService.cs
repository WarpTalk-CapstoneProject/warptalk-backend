using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IStripePaymentService
{
    Task<Result<string>> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Legacy: sets <c>cancel_at_period_end</c> on the workspace's active PLAN subscriptions found by
    /// metadata search. Only for plan rows with no linked Stripe subscription id (bought before
    /// #466); never touches an add-on's subscription (WT-878). A linked plan row goes through
    /// <see cref="SetPlanSubscriptionCancelAtPeriodEndAsync"/> instead.
    /// </summary>
    Task<Result<bool>> CancelSubscriptionAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// WT-878: auto-renew off (<c>true</c>) or back on (<c>false</c>) for ONE plan subscription,
    /// addressed by the Stripe subscription id stored on the plan row. Returns Stripe's status.
    /// </summary>
    Task<Result<string>> SetPlanSubscriptionCancelAtPeriodEndAsync(
        string stripeSubscriptionId,
        bool cancelAtPeriodEnd,
        CancellationToken cancellationToken = default);
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
