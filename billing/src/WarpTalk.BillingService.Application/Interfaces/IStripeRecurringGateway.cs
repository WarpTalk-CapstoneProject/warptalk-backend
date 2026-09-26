using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// #466 — the Stripe side of a card customer's recurring plan. Implemented in Infrastructure over
/// the Stripe SDK. Nothing here deletes a Stripe object: a subscription is CANCELED (it stays in
/// Stripe with its invoices), and a price that no longer matches the plan is ARCHIVED.
/// </summary>
public interface IStripeRecurringGateway
{
    bool IsConfigured { get; }

    /// <summary>
    /// The recurring Stripe Price that sells one <paramref name="billingCycle"/> of
    /// <paramref name="plan"/> at <paramref name="amount"/> <paramref name="currency"/>. Reuses the
    /// stored price when it still matches, otherwise archives it and creates (or re-finds, by lookup
    /// key) the right one. MUTATES the plan's Stripe fields and never saves: the caller commits.
    /// </summary>
    Task<Result<string>> EnsurePlanPriceAsync(
        Plan plan,
        string billingCycle,
        decimal amount,
        string currency,
        CancellationToken ct = default);

    /// <summary>Stripe's view of a subscription, with the card on file (brand/last4 only).</summary>
    Task<Result<StripeRecurringSnapshot>> GetSubscriptionAsync(string stripeSubscriptionId, CancellationToken ct = default);

    /// <summary>Auto-renew off (<c>true</c>) or back on (<c>false</c>) without ending the paid period.</summary>
    Task<Result<StripeRecurringSnapshot>> SetCancelAtPeriodEndAsync(string stripeSubscriptionId, bool cancelAtPeriodEnd, CancellationToken ct = default);

    /// <summary>Stops all further charges now (no proration, no final invoice). Used when dunning ran out or a plan was replaced.</summary>
    Task<Result> CancelNowAsync(string stripeSubscriptionId, CancellationToken ct = default);

    /// <summary>
    /// A Stripe billing-portal session where the customer updates the card. Returns its URL. The
    /// return URL is ALWAYS on our own site (the configured checkout origin): the caller picks only
    /// a path, so this can never bounce someone to a foreign page.
    /// </summary>
    Task<Result<string>> CreateBillingPortalUrlAsync(string stripeCustomerId, string? returnPath, CancellationToken ct = default);
}

/// <summary>#466: what the billing page shows about a Stripe subscription. Never card data beyond brand and last4.</summary>
public sealed record StripeRecurringSnapshot(
    string Id,
    string Status,
    bool CancelAtPeriodEnd,
    DateTime? CurrentPeriodEnd,
    string? CustomerId,
    decimal? NextChargeAmount,
    string? NextChargeCurrency,
    DateTime? NextChargeAt,
    string? CardBrand,
    string? CardLast4,
    int? CardExpMonth,
    int? CardExpYear);
