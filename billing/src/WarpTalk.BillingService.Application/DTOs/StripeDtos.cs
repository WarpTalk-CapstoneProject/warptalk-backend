namespace WarpTalk.BillingService.Application.DTOs;

public record StripeWebhookEvent(
    string id,
    string type,
    StripeEventData data
);

public record StripeEventData(
    StripeCheckoutSession @object
);

public record SimulatedPaymentResponse(
    string Message,
    int AddedCredits,
    decimal NewBalance,
    StripeCheckoutSession StripeData
);

public record StripeCheckoutSession(
    string id,
    long amount_total,
    string currency,
    string payment_status,
    string? payment_intent,
    string? client_reference_id
);


