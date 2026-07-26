namespace WarpTalk.BillingService.Domain.Constants;

public static class InvoiceConstants
{
    public static class InvoiceStatuses
    {
        public const string Draft = "draft";
        public const string Open = "open";
        public const string Paid = "paid";
        public const string Void = "void";
        public const string Uncollectible = "uncollectible";
    }

    public static class Formats
    {
        public const string InvoiceNumberPrefix = "INV-";
        public const string StripeInvoiceUrlTemplate = "https://stripe.com/invoice/{0}";
    }

    public static class Defaults
    {
        public const string EmptyLineItems = "[]";
        public const decimal VatRate = 0.10m;
    }

    public static class BillingReasons
    {
        public const string SubscriptionCycle = "subscription_cycle";
        public const string SubscriptionCreate = "subscription_create";
    }

    public static class LineItemTypes
    {
        public const string Subscription = "subscription";
        public const string Overage = "overage";
    }

    public static class LineItemDescriptions
    {
        public const string UsageOverCommittedCredits = "Usage over committed credits";
    }
}
