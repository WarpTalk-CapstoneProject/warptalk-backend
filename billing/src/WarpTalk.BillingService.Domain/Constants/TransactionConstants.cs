namespace WarpTalk.BillingService.Domain.Constants;

public static class TransactionConstants
{
    public static class RefundStatuses
    {
        public const string Pending = "pending";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    public static class TransactionStatuses
    {
        public const string Pending = "pending";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Refunded = "refunded";
        public const string Cancelled = "cancelled";
    }

    public static class ReferenceTypes
    {
        public const string StripePayment = "stripe_payment";
        public const string CreditReservation = "CreditReservation";
        public const string Payment = "payment";
        public const string ManualAdjustment = "manual_adjustment";
        public const string UsageRecord = "usage_record";
        public const string AggregatedBatch = "AggregatedBatch";
    }

    public static class TransactionTypes
    {
        public const string Consume = "consume";
        public const string TopUp = "top_up";
        public const string Adjustment = "adjustment";
        public const string Refund = "refund";
    }
}
