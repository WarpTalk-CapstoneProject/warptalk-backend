using System;
using System.Collections.Generic;

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

    /// <summary>
    /// #466: who owns a subscription's end-of-period transition. One owner per row, so the
    /// cycle-close worker, the expiry sweep and the Stripe webhooks can never act on the same row.
    /// Stored in subscriptions.renewal_mode (CHECK-constrained to these three).
    /// </summary>
    public static class RenewalModes
    {
        /// <summary>Contract / enterprise: BillingCycleWorker invoices and grants the next cycle.</summary>
        public const string Invoice = "invoice";

        /// <summary>A Stripe Subscription charges the saved card; <c>invoice.paid</c> renews.</summary>
        public const string Stripe = "stripe";

        /// <summary>Nothing renews it: a one-off card purchase or a trial. The sweep ends it.</summary>
        public const string None = "none";
    }

    /// <summary>
    /// #466: Stripe's subscription statuses, and which of them still mean "Stripe will send a
    /// renewal or a failure for this". A canceled / incomplete_expired subscription will never
    /// invoice again, so the local sweep takes the row back.
    /// </summary>
    public static class StripeSubscriptionStatuses
    {
        public const string Active = "active";
        public const string Trialing = "trialing";
        public const string PastDue = "past_due";
        public const string Incomplete = "incomplete";
        public const string Unpaid = "unpaid";
        public const string Canceled = "canceled";
        public const string IncompleteExpired = "incomplete_expired";
        public const string Paused = "paused";

        public static readonly IReadOnlySet<string> Ended = new HashSet<string>(StringComparer.Ordinal)
        {
            Canceled,
            IncompleteExpired,
        };
    }

    /// <summary>#466: dunning after a failed renewal charge.</summary>
    public static class Dunning
    {
        /// <summary>Platform setting holding the grace window, in days.</summary>
        public const string GraceDaysKey = WarpTalk.Shared.PlatformSettings.PlatformSettingsCatalog.DunningGraceDays;

        public const int DefaultGraceDays = 7;

        /// <summary>
        /// Extra time the sweep gives a Stripe-owned row past its period end before assuming the
        /// webhook will never come. Stripe finalizes a renewal invoice about an hour after the period
        /// ends; this is the safety net for a lost webhook, not the normal path.
        /// </summary>
        public static readonly TimeSpan StripeRenewalSafetyMargin = TimeSpan.FromDays(1);

        public const int FailureReasonMaxLength = 500;
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

        /// <summary>
        /// The workspace has no live subscription: it expired, or was superseded, without a
        /// renewal. NEVER stored in subscriptions.suspended_reason — that column's CHECK allows the
        /// three reasons above, and an ended row's own service state is not what changed. It is the
        /// reason settle_usage_charge returns for an inactive row, the one billing_worker publishes
        /// when a room's workspace has no subscription at all, and the one the expiry sweep writes
        /// into the Redis AI-service state that Start Translation reads.
        /// </summary>
        public const string SubscriptionExpired = "subscription_expired";
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

        /// <summary>Value a plan row carries when nobody stated one. Mirrors the
        /// <c>subscription.plans.max_languages</c> column default (2) and the
        /// <see cref="Entities.Plan.MaxLanguages"/> property initializer — changing this changes
        /// what a newly created plan gets, so it must stay in step with the SQL default.</summary>
        public const int MaxLanguages = 2;

        /// <summary>Highest value an admin may store in <c>max_languages</c>. WT-262: this is the
        /// real meaning of the former <c>FeatureAccess.DefaultMaxLanguages</c> — it was named a
        /// "default" but was only ever used as the validation ceiling in PlanService, and
        /// separately as a fabricated response value in the gRPC mapper. The mapper now reads the
        /// column, so the ceiling is the only surviving use and it lives here, next to the default
        /// it bounds.</summary>
        public const int MaxLanguagesCeiling = 3;
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

        /// <summary>The seeded Enterprise plan buys the maximum the platform allows, so this is
        /// deliberately an alias of the ceiling rather than an independent 3. Production's
        /// Enterprise row has max_languages = 3 and this keeps it there.</summary>
        public const int MaxLanguages = PlanDefaults.MaxLanguagesCeiling;
    }

    public static class FeatureAccess
    {
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
