namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// Internal Stripe webhook and Checkout Session contracts.
/// These types contain only the provider fields used by Billing Service.
/// </summary>

public record StripeWebhookEvent(
    string id,
    string type,
    StripeEventData data
);

public record StripeEventData(
    StripeCheckoutSession @object
);

public record StripeCheckoutSession(
    string id,
    long amount_total,
    string currency,
    string payment_status,
    string? payment_intent,
    string? client_reference_id
);

public record StripeCreateCheckoutSessionRequest(
    long amountTotal,
    string currency,
    string clientReferenceId,
    string cancelUrl,
    string successUrl
);

public record StripeCreateCheckoutSessionResponse(
    string id,
    long amountTotal,
    string currency,
    string paymentStatus,
    string checkoutUrl
);
