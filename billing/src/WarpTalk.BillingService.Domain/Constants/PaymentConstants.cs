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

        /// <summary>
        /// WT-429. Buying credits outright, outside the subscription cycle.
        ///
        /// The web has posted this string since the top-up UI was built, and NOTHING matched it:
        /// no handler claimed it, PaymentAppService skipped the grant in silence, and the request
        /// still wrote a payment row and issued an invoice. Money in, no credits — which is why
        /// the button was switched off rather than fixed at the time (#190).
        /// </summary>
        public const string CreditTopUp = "CreditTopUp";

        /// <summary>G11: a catalog credit pack (subscription.credit_packs), priced from the pack.</summary>
        public const string CreditPack = "CreditPack";

        /// <summary>
        /// G11: a catalog add-on — its own Stripe subscription, never the plan's. The webhook maps
        /// the add-on subscription's later events onto the three types below so that none of them
        /// can reach the plan's handlers (a deleted add-on subscription must not cancel the plan).
        /// </summary>
        public const string AddOn = "AddOn";
        public const string AddOnRenewal = "AddOnRenewal";
        public const string AddOnUpdate = "AddOnUpdate";
        public const string AddOnCancellation = "AddOnCancellation";

        public static readonly IReadOnlySet<string> AddOnLifecycleTypes = new HashSet<string>
        {
            AddOn,
            AddOnRenewal,
            AddOnUpdate,
            AddOnCancellation
        };

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
        /// <summary>
        /// WT-699 / TC3906: the checkout this payment was waiting on expired unpaid. Distinct from
        /// Cancelled on purpose — Cancelled routes to CancellationPaymentEventHandler, which ends
        /// the workspace's SUBSCRIPTION; an abandoned checkout must never do that.
        /// </summary>
        public const string Expired = "expired";
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

        /// <summary>
        /// WT-429: how many credits this top-up buys, decided SERVER-side and carried on the
        /// Stripe session so the webhook and the return path grant the same number without
        /// re-deriving it from the amount (which would make the rate a client input).
        /// </summary>
        public const string Credits = "Credits";
    }

    public static class StripeEvents
    {
        public const string CheckoutSessionCompleted = "checkout.session.completed";
        /// <summary>
        /// WT-699 / TC3906: a Checkout Session the buyer abandoned. Stripe expires it (24h by
        /// default) and it can never be paid afterwards.
        /// </summary>
        public const string CheckoutSessionExpired = "checkout.session.expired";
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
