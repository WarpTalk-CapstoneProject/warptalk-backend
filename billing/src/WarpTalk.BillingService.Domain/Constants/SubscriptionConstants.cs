namespace WarpTalk.BillingService.Domain.Constants;

public static class SubscriptionConstants
{
    public static class SubscriptionStatuses
    {
        public const string Pending = "pending";
        public const string Active = "active";
        public const string Cancelled = "cancelled";
        public const string Expired = "expired";
        public const string Suspended = "suspended";
    }

    public static class BillingCycles
    {
        public const string Monthly = "monthly";
        public const string Semiannual = "semiannual";
        public const string Yearly = "yearly";
    }
}
