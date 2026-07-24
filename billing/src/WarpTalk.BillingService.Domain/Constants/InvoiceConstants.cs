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
}
