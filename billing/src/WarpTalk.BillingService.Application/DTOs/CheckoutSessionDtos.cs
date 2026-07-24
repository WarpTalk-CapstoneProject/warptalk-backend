using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.DTOs;

public record CreateCheckoutSessionRequest(
    Guid UserId,
    Guid WorkspaceId,
    decimal Amount,
    string Currency = PaymentConstants.Currencies.Usd,
    string PaymentType = "",
    string PlanSlug = "",
    string BillingCycle = ""
);

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
