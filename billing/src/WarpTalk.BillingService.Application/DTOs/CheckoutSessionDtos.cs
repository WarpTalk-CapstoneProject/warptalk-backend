using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Implements <see cref="IWorkspaceScopedRequest"/> so the workspace-role filter can find the
/// workspace this checkout belongs to: the id travels in the body, not the route.
/// </summary>
public record CreateCheckoutSessionRequest(
    Guid UserId,
    Guid WorkspaceId,
    decimal Amount,
    string Currency = PaymentConstants.Currencies.Usd,
    string PaymentType = "",
    string PlanSlug = "",
    string BillingCycle = "",
    /// <summary>
    /// WT-429, top-ups only: how many credits the buyer asked for. The PRICE is computed from
    /// this server-side against billing_pricing_config, and <see cref="Amount"/> is overwritten
    /// with the result — a client that names its own price would be naming its own exchange rate.
    /// </summary>
    int Credits = 0,
    /// <summary>
    /// WT-545: the email of the account that started this checkout, stamped by the API from the
    /// caller's token — never sent by the client. Stripe locks its email field to it, so the
    /// hosted page belongs to the buyer's account instead of collecting whatever address the
    /// person holding a forwarded link types in.
    ///
    /// Empty is tolerated (an account with no email claim still gets to pay); Stripe simply
    /// falls back to asking.
    /// </summary>
    string BuyerEmail = ""
) : IWorkspaceScopedRequest;
public record StripePaymentEventRequest(
    string StripeSessionId,
    string PaymentIntentId,
    decimal Amount,
    string Currency,
    string UserIdStr,
    string WorkspaceIdStr,
    string PaymentType,
    string Status,
    string FailureReason = "",
    string InvoiceUrl = "",
    string InvoicePdf = "",
    string PlanSlug = "",
    string BillingCycle = "",
    /// <summary>WT-429: credits to grant, read back off the Stripe session metadata.</summary>
    int Credits = 0
);

public record CheckoutSessionDto(
    string Id,
    long? AmountTotal,
    string Currency,
    IReadOnlyDictionary<string, string> Metadata,
    string PaymentStatus,
    string Status,
    string PaymentIntentId
);
