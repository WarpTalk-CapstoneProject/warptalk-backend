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
        // Trial is sized for a short proof-of-value: enough credits for several meetings,
        // no paid overage, and a two-week evaluation window.
        public const int Credits = 20000;
        public const int DurationDays = 14;
        public const int OverageCapCredits = 0;
    }

    public static class PlanDefaults
    {
        // Contract defaults are minimum safety rails. Admins can override the actual
        // commercial plan values through the Enterprise baseline screen.
        public const decimal MinimumVndPlanPrice = 15000m;
        public const decimal MinimumUsdPlanPrice = 0.50m;
        public const decimal PriceFloorPerCredit = 2.60m;
        public const decimal OveragePricePerCredit = 4.0000m;
        public const int InvoiceTermsDays = 15;
        public const int InvoiceGraceHours = 360;
        public const int MaxParticipants = 2;
        public const int MaxLanguages = 2;
    }

    public static class RateCardDefaults
    {
        // Seed values for initial admin pricing config. Live pricing rows can override
        // these values through the rate-card admin workflow.
        public const decimal FxRateUsdVnd = 26300m;
        public const decimal CreditValueVnd = 4m;
        public const decimal SalesUsageWeight = 0.45m;
        public const decimal SalesMembersWeight = 0.15m;
        public const decimal SalesLanguagesWeight = 0.15m;
        public const decimal SalesAiServicesWeight = 0.25m;
        public const decimal DefaultOverageCapRatio = 0.15m;
    }

    public static class EnterpriseBaseline
    {
        // Baseline seed for the default Enterprise contract: about 700k monthly credits,
        // 15% extra-usage cap, NET-15 payment terms, and 15-day invoice grace window.
        public const decimal PriceVnd = 1900000m;
        public const int CreditsPerCycle = 700000;
        public const int OverageCapCredits = 105000;
        public const decimal OveragePricePerCredit = 4.0000m;
        public const int LowBalanceThresholdCredits = 140000;
        public const int RolloverCapCredits = 700000;
        public const int InvoiceTermsDays = 15;
        public const int InvoiceGraceHours = 360;
        public const int MaxParticipants = 500;
        public const int MaxLanguages = 3;
    }

    public static class FeatureAccess
    {
        public const int DefaultMaxLanguages = 3;
        public const string EmptyFeaturesJson = "{}";
        public const string GoogleMeetIntegration = "google_meet";
        public const string EnterpriseFeaturesJson =
            "{"
            + "\"voice_clone_limit_mins\":-1,"
            + "\"billing_model\":\"contract_template\","
            + "\"overage_policy\":\"invoice_after_cap\","
            + "\"external_integrations\":{\"google_meet\":true},"
            + "\"supported_external_platforms\":[\"google_meet\"]"
            + "}";
    }
}
