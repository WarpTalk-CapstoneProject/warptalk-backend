namespace WarpTalk.BillingService.Domain.Constants;

public static class PaymentConstants
{
    public static class Providers
    {
        public const string Stripe = "stripe";
        public const string TopUpSimulation = "top_up_simulation";
        public const string StripeSimulation = "stripe_simulation";
        public const string InternalInvoice = "internal_invoice";
    }

    public static class PaymentTypes
    {
        public const string CreditTopUp = "CreditTopUp";
        public const string Subscription = "Subscription";
        public const string SubscriptionRenewal = "SubscriptionRenewal";
        public const string SubscriptionUpdate = "SubscriptionUpdate";
        public const string InvoicePayment = "InvoicePayment";

        public static readonly IReadOnlySet<string> SubscriptionLifecycleTypes = new HashSet<string>
        {
            Subscription,
            SubscriptionRenewal,
            SubscriptionUpdate
        };
    }

    public static class PaymentStatuses
    {
        public const string Pending = "pending";
        public const string Paid = "paid";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string Refunded = "refunded";
        public const string Disputed = "disputed";
        public const string SubscriptionUpdated = "subscription_updated";
    }

    public static class PaymentMethods
    {
        public const string Card = "card";
        public const string Invoice = "invoice";
        public const string StripeUpgradeDirect = "Stripe Upgrade (Direct)";
        public const string StripeUpgradeSimulation = "Stripe Upgrade (Simulation)";
    }

    public static class Payments
    {
        public const string StatusPaid = "paid";
        public const string StatusUnpaid = "unpaid";
        public const string StatusNoPaymentRequired = "no_payment_required";
    }

    public static class Currencies
    {
        public const string Usd = "usd";
        public const string Vnd = "vnd";
        public const string VndAccounting = "VND";
    }

    public static class StripeMetadata
    {
        public const string UserId = "UserId";
        public const string WorkspaceId = "WorkspaceId";
        public const string PaymentType = "PaymentType";
        public const string PlanSlug = "PlanSlug";
        public const string BillingCycle = "BillingCycle";
    }

    public static class StripeEvents
    {
        public const string CheckoutSessionCompleted = "checkout.session.completed";
        public const string PaymentIntentPaymentFailed = "payment_intent.payment_failed";
        public const string ChargeRefunded = "charge.refunded";
        public const string ChargeDisputeCreated = "charge.dispute.created";
        public const string CustomerSubscriptionUpdated = "customer.subscription.updated";
        public const string CustomerSubscriptionDeleted = "customer.subscription.deleted";
        public const string InvoicePaid = "invoice.paid";
    }

    public static class StripePrefixes
    {
        public const string Session = "cs_";
        public const string Invoice = "in_";
        public const string PaymentIntent = "pi_";
        public const string MockSession = "mock_session_";
        public const string MockPaymentIntent = "mock_pi_";
    }

    public static class StripeStatuses
    {
        public const string Active = "active";
        public const string Succeeded = "succeeded";
        public const string RequiresPaymentMethod = "requires_payment_method";
        public const string Canceled = "canceled";
        public const string Paid = "paid";
        public const string Complete = "complete";
        public const string Pending = "pending";
        public const string Failed = "failed";
        public const string Unknown = "unknown";
    }

    public static class StripeModes
    {
        public const string Subscription = "subscription";
        public const string Payment = "payment";
    }

    public static class StripeProrationBehaviors
    {
        public const string AlwaysInvoice = "always_invoice";
    }

    public static class ProductNames
    {
        public const string CreditTopUp = "Credit Top-Up";
        public const string SubscriptionPlan = "Subscription Plan";
        public const string InvoicePayment = "Invoice Payment";
    }

    public static class PriceIntervals
    {
        public const string Month = "month";
        public const string Year = "year";
    }

    public static class StripeLimits
    {
        public const int MinimumTopUpCredits = 1500;
    }

    public static class StripeConfigKeys
    {
        public const string SecretKey = "Stripe:SecretKey";
        public const string WebhookSecret = "Stripe:WebhookSecret";
        public const string SuccessUrl = "Stripe:SuccessUrl";
        public const string CancelUrl = "Stripe:CancelUrl";
    }

    public static class StripePlaceholders
    {
        public const string SecretKeyPlaceholder = "sk_test_placeholder";
        public const string WebhookSecretPlaceholder = "whsec_test_secret";
        public const string DefaultPaymentFailureReason = "Payment failed";
        public const string DefaultPaymentFailureOrCanceledReason = "Payment failed or canceled";
        public const string DefaultStripeWebhookProductionSecretError = "Stripe webhook secret is not configured in production.";
        public const string WebhookSecretNotConfigured = "Webhook secret not configured";
        public const string UnknownWebhookSessionUrlToken = "{CHECKOUT_SESSION_ID}";
    }

    public static class StripeSearchQueries
    {
        public const string SubscriptionSearchTemplate = "metadata['{0}']:'{1}' AND status:'{2}'";
    }

    public static class StripeDefaultUrls
    {
        public const string MockSuccessUrl = "http://localhost:3000/workspace/payment/success?session_id={CHECKOUT_SESSION_ID}";
        public const string SandboxSuccessUrl = "http://localhost:3000/sandbox/workspace-billing?session_id={CHECKOUT_SESSION_ID}";
        public const string CancelUrl = "http://localhost:3000/payment-cancelled";
    }

    public static class StripeErrorMessages
    {
        public const string InvalidProviderTxIdFormat = "Invalid provider transaction ID format";
        public const string InvalidMockSessionPayload = "Invalid mock session payload.";
        public const string SessionNotFound = "Session not found.";
    }
}
