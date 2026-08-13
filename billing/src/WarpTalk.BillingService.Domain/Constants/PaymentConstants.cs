namespace WarpTalk.BillingService.Domain.Constants;

public static class PaymentConstants
{
    public static class Providers
    {
        public const string Stripe = "stripe";
        public const string InternalInvoice = "internal_invoice";
    }

    public static class PaymentTypes
    {
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
    }

    public static class Payments
    {
        public const string StatusPaid = "paid";
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
        public const string SubscriptionPlan = "Subscription Plan";
        public const string InvoicePayment = "Invoice Payment";
    }

    public static class PriceIntervals
    {
        public const string Month = "month";
        public const string Year = "year";
    }

    /// <summary>
    /// What the CLIENT calls a billing cycle, which is not what Stripe calls one.
    ///
    /// <see cref="PriceIntervals"/> is Stripe's vocabulary ("month"/"year") and is what goes out
    /// on a price. The plans page sends "monthly"/"yearly" — the two are never equal, and
    /// comparing them directly is the WT-370 bug. Read a cycle through
    /// <c>BillingCycleResolver</c>, never with <c>==</c> against PriceIntervals.
    /// </summary>
    public static class BillingCycles
    {
        public const string Monthly = "monthly";
        public const string Yearly = "yearly";

        public static readonly string[] MonthlySpellings = { Monthly, PriceIntervals.Month };
        public static readonly string[] YearlySpellings =
        {
            Yearly,
            PriceIntervals.Year,
            "annual",
            "annually"
        };
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
        public const string SecretKeyPlaceholder = "";
        public const string WebhookSecretPlaceholder = "";
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

    public static class StripeErrorMessages
    {
        public const string InvalidProviderTxIdFormat = "Invalid provider transaction ID format";
        public const string SessionNotFound = "Session not found.";
        public const string SecretKeyNotConfigured = "Stripe secret key is not configured.";
        public const string CheckoutUrlsNotConfigured = "Stripe checkout success and cancel URLs are not configured.";
    }
}
