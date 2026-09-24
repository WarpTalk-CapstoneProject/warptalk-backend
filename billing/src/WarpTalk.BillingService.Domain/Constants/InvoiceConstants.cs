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

        /// <summary>
        /// "issued" is the entity's and the schema's default status even though no constant above
        /// names it, so a filter that could not select it would hide real rows.
        /// </summary>
        public const string Issued = "issued";

        public static readonly string[] Filterable = [Draft, Issued, Open, Paid, Void, Uncollectible];
    }

    public static class Errors
    {
        public const string UnknownStatusFilter =
            "Unknown status. Expected one of: draft, issued, open, paid, void, uncollectible.";
        public const string UnknownSort =
            "Unknown sort. Expected one of: issued_desc, issued_asc, total_desc, total_asc, due_asc.";
        public const string InvalidCurrency = "currency must be a three-letter ISO 4217 code.";
        public const string InvalidTotalRange = "'minTotal' must not be greater than 'maxTotal'.";
    }

    public static class Formats
    {
        public const string InvoiceNumberPrefix = "INV-";
        public const string StripeInvoiceUrlTemplate = "https://stripe.com/invoice/{0}";
    }

    public static class Defaults
    {
        public const string EmptyLineItems = "[]";
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
        public const string UsageBreakdown = "usage_breakdown";
    }

    public static class LineItemDescriptions
    {
        public const string UsageOverCommittedCredits = "Usage over committed credits";
    }
}
