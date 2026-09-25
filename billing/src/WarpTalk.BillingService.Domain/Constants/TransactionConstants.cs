namespace WarpTalk.BillingService.Domain.Constants;

public static class TransactionConstants
{
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
        public const string Payment = "payment";
        public const string ManualAdjustment = "manual_adjustment";
        public const string UsageRecord = "usage_record";
        public const string AggregatedBatch = "AggregatedBatch";

        /// <summary>A freeze or forfeit at the end of the subscription the row belongs to.</summary>
        public const string SubscriptionExpiry = "subscription_expiry";

        /// <summary>
        /// A freeze of the WHOLE balance of a subscription that ended before the forfeit policy took
        /// effect (grandfathered: nothing forfeited). The type is still credit_freeze.
        /// </summary>
        public const string CreditFreezeGrandfathered = "credit_freeze_grandfathered";

        /// <summary>Frozen credits moved into a live subscription; ReferenceId is the row they came from.</summary>
        public const string FrozenCreditRelease = "frozen_credit_release";

        /// <summary>An audited admin adjustment of an ended subscription's FROZEN credits.</summary>
        public const string FrozenCreditAdjustment = "frozen_credit_adjustment";
    }

    public static class TransactionTypes
    {
        public const string Consume = "consume";
        public const string TopUp = "top_up";
        public const string Adjustment = "adjustment";

        /// <summary>Spendable balance moved into the frozen bucket when the subscription ended.</summary>
        public const string CreditFreeze = "credit_freeze";

        /// <summary>Frozen credits moved back into a live subscription's spendable balance.</summary>
        public const string CreditUnfreeze = "credit_unfreeze";

        /// <summary>Plan credits above the plan's rollover cap, removed when the subscription ended.</summary>
        public const string CreditForfeit = "credit_forfeit";
    }
}
