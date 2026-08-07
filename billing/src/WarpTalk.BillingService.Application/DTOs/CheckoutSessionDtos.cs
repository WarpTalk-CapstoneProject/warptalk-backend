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
    string BillingCycle = ""
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
    string BillingCycle = ""
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
