using Stripe;
using Stripe.Checkout;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.API.Services;

public sealed class StripeBillingGateway : IStripeBillingGateway
{
    private readonly IConfiguration _configuration;

    public StripeBillingGateway(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<string> CreateCheckoutAsync(
        ResolvedCheckout checkout,
        CancellationToken cancellationToken = default)
    {
        ConfigureStripe();

        var metadata = new Dictionary<string, string>
        {
            ["UserId"] = checkout.UserId.ToString(),
            ["WorkspaceId"] = checkout.WorkspaceId.ToString(),
            ["PaymentType"] = checkout.PaymentType,
            ["PlanSlug"] = checkout.PlanSlug,
            ["BillingCycle"] = checkout.BillingCycle
        };

        var subscription = checkout.PaymentType == "Subscription";
        var options = new SessionCreateOptions
        {
            PaymentMethodTypes = ["card"],
            Mode = subscription ? "subscription" : "payment",
            SuccessUrl = RequiredConfiguration("Stripe:SuccessUrl"),
            CancelUrl = RequiredConfiguration("Stripe:CancelUrl"),
            Metadata = metadata,
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = checkout.Currency,
                        UnitAmount = ToMinorUnits(checkout.Amount, checkout.Currency),
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = checkout.ProductName
                        },
                        Recurring = subscription
                            ? new SessionLineItemPriceDataRecurringOptions
                            {
                                Interval = checkout.BillingCycle.Equals("yearly", StringComparison.OrdinalIgnoreCase)
                                    ? "year"
                                    : "month",
                                IntervalCount = 1
                            }
                            : null
                    }
                }
            ],
            SubscriptionData = subscription
                ? new SessionSubscriptionDataOptions { Metadata = metadata }
                : null,
            PaymentIntentData = subscription
                ? null
                : new SessionPaymentIntentDataOptions { Metadata = metadata }
        };

        var session = await new SessionService().CreateAsync(
            options,
            cancellationToken: cancellationToken);

        return session.Url
            ?? throw new InvalidOperationException("Stripe did not return a checkout URL.");
    }

    public async Task<CheckoutSessionDto?> GetCheckoutSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!sessionId.StartsWith("cs_", StringComparison.Ordinal))
            return null;

        ConfigureStripe();
        var session = await new SessionService().GetAsync(
            sessionId,
            cancellationToken: cancellationToken);

        return new CheckoutSessionDto(
            session.Id,
            session.AmountTotal,
            session.Currency ?? string.Empty,
            session.Metadata,
            session.PaymentStatus ?? string.Empty,
            session.Status ?? string.Empty,
            session.PaymentIntentId);
    }

    public ProcessPaymentEventRequest? ParseWebhook(string payload, string signature)
    {
        ConfigureStripe();
        var webhookSecret = RequiredConfiguration("Stripe:WebhookSecret");
        var stripeEvent = EventUtility.ConstructEvent(
            payload,
            signature,
            webhookSecret,
            throwOnApiVersionMismatch: false);

        return stripeEvent.Type switch
        {
            "checkout.session.completed" when stripeEvent.Data.Object is Session session
                => FromCheckoutSession(session, "paid"),
            "payment_intent.payment_failed" when stripeEvent.Data.Object is PaymentIntent intent
                => FromPaymentIntent(intent),
            "charge.refunded" when stripeEvent.Data.Object is Charge charge
                => FromCharge(charge, "refunded"),
            "charge.dispute.created" when stripeEvent.Data.Object is Dispute dispute
                => FromDispute(dispute),
            "customer.subscription.deleted" when stripeEvent.Data.Object is Subscription subscription
                => FromSubscription(subscription),
            _ => null
        };
    }

    private ProcessPaymentEventRequest FromCheckoutSession(Session session, string status)
    {
        var metadata = session.Metadata;
        return NewEvent(
            session.Id,
            !string.IsNullOrWhiteSpace(session.InvoiceId) ? session.InvoiceId : session.PaymentIntentId,
            FromMinorUnits(session.AmountTotal ?? 0, session.Currency),
            session.Currency,
            metadata,
            status);
    }

    private ProcessPaymentEventRequest FromPaymentIntent(PaymentIntent intent)
        => NewEvent(
            string.Empty,
            intent.Id,
            FromMinorUnits(intent.Amount, intent.Currency),
            intent.Currency,
            intent.Metadata,
            "failed",
            intent.LastPaymentError?.Message ?? "Stripe payment failed.");

    private ProcessPaymentEventRequest FromCharge(Charge charge, string status)
        => NewEvent(
            string.Empty,
            charge.PaymentIntentId,
            FromMinorUnits(charge.AmountRefunded, charge.Currency),
            charge.Currency,
            charge.Metadata,
            status);

    private ProcessPaymentEventRequest FromDispute(Dispute dispute)
        => NewEvent(
            string.Empty,
            dispute.PaymentIntentId ?? dispute.ChargeId,
            FromMinorUnits(dispute.Amount, dispute.Currency),
            dispute.Currency,
            new Dictionary<string, string>(),
            "disputed");

    private ProcessPaymentEventRequest FromSubscription(Subscription subscription)
        => NewEvent(
            string.Empty,
            subscription.Id,
            0,
            "vnd",
            subscription.Metadata,
            "cancelled");

    private static ProcessPaymentEventRequest NewEvent(
        string sessionId,
        string? providerTransactionId,
        decimal amount,
        string? currency,
        IReadOnlyDictionary<string, string> metadata,
        string status,
        string failureReason = "")
        => new()
        {
            StripeSessionId = sessionId,
            ProviderTransactionId = providerTransactionId ?? string.Empty,
            Amount = (double)amount,
            Currency = currency ?? "vnd",
            UserId = Metadata(metadata, "UserId"),
            WorkspaceId = Metadata(metadata, "WorkspaceId"),
            PaymentType = Metadata(metadata, "PaymentType"),
            PlanSlug = Metadata(metadata, "PlanSlug"),
            BillingCycle = Metadata(metadata, "BillingCycle"),
            Status = status,
            FailureReason = failureReason
        };

    private void ConfigureStripe()
    {
        StripeConfiguration.ApiKey = RequiredConfiguration("Stripe:SecretKey");
    }

    private string RequiredConfiguration(string key)
        => _configuration[key] is { Length: > 0 } value
            && !value.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
                ? value
                : throw new InvalidOperationException($"{key} is not configured.");

    private static long ToMinorUnits(decimal amount, string currency)
        => IsZeroDecimal(currency) ? checked((long)amount) : checked((long)(amount * 100m));

    private static decimal FromMinorUnits(long amount, string? currency)
        => IsZeroDecimal(currency) ? amount : amount / 100m;

    private static bool IsZeroDecimal(string? currency)
        => string.Equals(currency, "vnd", StringComparison.OrdinalIgnoreCase);

    private static string Metadata(
        IReadOnlyDictionary<string, string> metadata,
        string key)
        => metadata.TryGetValue(key, out var value) ? value : string.Empty;
}
