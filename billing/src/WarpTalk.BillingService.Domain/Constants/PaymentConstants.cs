namespace WarpTalk.BillingService.Domain.Constants;

public static class PaymentConstants
{
    public static class Providers
    {
        public const string Stripe = "stripe";
        public const string TopUpSimulation = "top_up_simulation";
        public const string StripeSimulation = "stripe_simulation";
    }

    public static class PaymentTypes
    {
        public const string CreditTopUp = "CreditTopUp";
        public const string Subscription = "Subscription";
        public const string SubscriptionRenewal = "SubscriptionRenewal";
        public const string SubscriptionUpdate = "SubscriptionUpdate";
    }

    public static class PaymentStatuses
    {
        public const string Pending = "pending";
        public const string Paid = "paid";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string Refunded = "refunded";
    }

    public static class PaymentMethods
    {
        public const string Card = "card";
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
    }

    public static class StripeSimulation
    {
        public const string SessionPrefix = "cs_test_";
        public const string PaymentIntentPrefix = "pi_";
        public const string EventPrefix = "evt_";
    }

    public static class StripeLimits
    {
        public const int MinimumTopUpCredits = 1500;
    }
}
