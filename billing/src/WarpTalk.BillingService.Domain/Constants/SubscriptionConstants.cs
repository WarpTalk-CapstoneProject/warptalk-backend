namespace WarpTalk.BillingService.Domain.Constants;

public static class SubscriptionConstants
{
    public static class SubscriptionStatuses
    {
        public const string None = "none";
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

    public static class ServiceStates
    {
        public const string Healthy = "healthy";
        public const string LowBalance = "low_balance";
        public const string InOverage = "in_overage";
        public const string Suspended = "suspended";
    }

    public static class SuspendedReasons
    {
        public const string OverageCap = "overage_cap";
        public const string InvoiceOverdue = "invoice_overdue";
        public const string TrialEnded = "trial_ended";
    }

    public static class Tiers
    {
        public const string NoActivePlan = "No Active Plan";
        public const string Startup = "Startup";
        public const string Enterprise = "Enterprise";
    }

    public static class PlanSlugs
    {
        public const string Enterprise = "enterprise";
    }

    public static class TrialDefaults
    {
        public const int Credits = 20000;
        public const int DurationDays = 14;
        public const int OverageCapCredits = 0;
    }

    public static class PlanDefaults
    {
        public const decimal PriceFloorPerCredit = 2.60m;
        public const decimal OveragePricePerCredit = 4.0000m;
        public const int InvoiceTermsDays = 15;
        public const int InvoiceGraceHours = 360;
        public const int MaxParticipants = 2;
        public const int MaxLanguages = 2;
    }

    public static class FeatureAccess
    {
        public const int DefaultMaxLanguages = 3;
        public const string EmptyFeaturesJson = "{}";
    }
}
