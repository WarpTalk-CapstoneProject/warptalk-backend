namespace WarpTalk.BillingService.Domain.Constants;

public static class BillingMessageConstants
{
    public static class SuccessMessages
    {
        public const string SubscriptionPlanActivationTemplate = "Subscription Plan Activation: {0}";
        public const string SubscriptionPlanRenewalTemplate = "{0} — Renewal {1:yyyy-MM-dd}";
    }

    public static class NotificationTitles
    {
        public const string BillingInvoiceReminder = "Billing invoice reminder";
    }

    public static class NotificationMetadataKeys
    {
        public const string InvoiceId = "invoice_id";
        public const string InvoiceNumber = "invoice_number";
        public const string ReminderKind = "reminder_kind";
    }

    public static class AdjustmentMessages
    {
        public const string PlanUpgradeDirect = "Plan upgrade to {0} (Stripe Direct)";
    }

    public static class UsageMessages
    {
        public const string AiUsageTemplate = "AI Usage: {0} by User {1}";
        public const string AiAssistantDetailsTemplate = "AI Assistant: {0} | Model: {1} | In: {2} tokens @ {3}/1K | Out: {4} tokens @ {5}/1K";
        public const string DocumentTranslationTemplate = "Document Translation to {0}";
        public const string AggregatedBatchDescription = "Aggregated batch";
        public const string AggregatedChargeDescriptionTemplate = "Aggregated {0}";
    }

    public static class AnalyticsMessages
    {
        public const string WorkspaceNameTemplate = "Workspace {0}";
        public const string UnknownWorkspace = "Unknown Workspace";
        public const string HighConsumptionAlertTemplate = "Unusually high consumption: {0} credits in 24h";
    }

    public static class PlanAuditMessages
    {
        public const string PriceChanged = "Price changed from {0:N0} to {1:N0} {2}";
        public const string CreditsChanged = "Credits per cycle changed from {0:N0} to {1:N0}";
        public const string MaxParticipantsChanged = "Max participants changed from {0} to {1}";

        public const string NameChanged = "Name changed from '{0}' to '{1}'";
        public const string UnknownPlan = "Unknown Plan";
        public const string Created = "Plan Created";
        public const string Updated = "Plan Updated";
    }

    public static class ValidationMessages
    {
        public const string EventIdRequired = "Event id is required.";
        public const string ConsumerRequired = "Consumer is required.";
        public const string EventTypeRequired = "Event type is required.";
    }

    public static class Plan
    {

        public static class Actions
        {
            public const string Created = "created";
            public const string Updated = "updated";
            public const string Deactivated = "deactivated";
        }
    }

    public static class Subscription
    {
        public static class Actions
        {
            public const string Activated = "activated";
        }
        public const string UnknownPlan = "Unknown Plan";
    }

    public static class LogMessages
    {
        public const string FailedToResolveWorkspaceNames = "Failed to resolve workspace names for global credit history";
        public const string FailedToPublishRealtimeCreditUpdate = "Failed to publish realtime credit update for UserId {UserId}";
        public const string FailedToPublishRealtimeSubscriptionUpdate = "Failed to publish realtime update for user {UserId}";
        public const string FailedToPublishPlanUpdateBroadcast = "Failed to publish plan update broadcast for plan {PlanName}";
        public const string FailedToPublishRealtimeCreditUpdateForWorkspace = "Failed to publish realtime credit update for WorkspaceId {WorkspaceId}";
        public const string ErrorGettingPaymentHistory = "Error getting payment history for WorkspaceId {WorkspaceId}";
        public const string ErrorCreatingPayment = "Error creating payment for SubscriptionId {SubscriptionId}";
        public const string ErrorUpdatingPaymentStatus = "Error updating payment status for PaymentId {PaymentId}";
        public const string WebhookSubscriptionNotFound = "HandleWebhookAsync: Subscription {SubscriptionId} not found for paid payment {PaymentId}. Aborting activation.";
        public const string WebhookPlanNotFound = "HandleWebhookAsync: Plan {PlanId} not found for subscription {SubscriptionId}. Aborting activation.";
        public const string ErrorHandlingWebhook = "Error handling webhook for OrderCode {OrderCode}";
        public const string CheckoutSessionReceivedDiagnostic = "[PAYMENTS-DIAGNOSTIC] Received checkout request: Amount={Amount}, Currency='{Currency}', PaymentType='{PaymentType}', WorkspaceId={WorkspaceId}";
        public const string CheckoutSessionFallbackProcessing = "[PAYMENTS-LOCAL-FALLBACK] Processing checkout session {SessionId} directly via success page API call";
        public const string StripeWebhookException = "Stripe exception during webhook handling";
        public const string StripeWebhookUnexpectedError = "Unexpected error during webhook handling";
        public const string ProcessPaymentEventCalled = "ProcessPaymentEventAsync called for session {SessionId}, status: {Status}";
        public const string InvalidWorkspaceIdInMetadata = "Invalid workspace ID in payment metadata: {WorkspaceId}";
        public const string StripePaymentAlreadyProcessed = "Stripe payment {ProviderTxId} is already processed as Paid. Skipping.";
        public const string DefaultFreePlanNotFound = "Default free plan not found for subscription auto-provisioning.";
        public const string NoActiveSubscriptionForTopUp = "Credit top-up aborted: no active subscription found for workspace {WorkspaceId}. Subscribe first before topping up.";
        public const string PlanNotFoundForSubscription = "Plan {PlanSlug} not found for subscription processing.";
        public const string FailedToUpdateStripeSubChangePlan = "Failed to update subscription on Stripe for WorkspaceId {WorkspaceId} during change plan.";
        public const string FailedToCancelOldStripeSubChangePlan = "Failed to cancel old subscription on Stripe for WorkspaceId {WorkspaceId} during change plan.";
        public const string ErrorCancellingStripeSubscription = "Failed to cancel subscription on Stripe for WorkspaceId {WorkspaceId}";
        public const string FailedToResolveWorkspaceNamesGlobalSub = "Failed to resolve workspace names for global subscriptions history";
        public const string ErrorFetchingGlobalSubscriptions = "Error fetching global subscriptions";
        public const string ErrorCreatingSubscription = "Error creating subscription for WorkspaceId {WorkspaceId} and PlanId {PlanId}";
        public const string ErrorGeneratingBillingReport = "Error generating billing report for WorkspaceId {WorkspaceId}";
        public const string ErrorGettingUsageChart = "Error getting usage chart for WorkspaceId {WorkspaceId}";
        public const string ErrorGettingFeatureAdoption = "Error getting feature adoption for WorkspaceId {WorkspaceId}";
        public const string ErrorGettingGlobalMetrics = "Error getting global metrics";
        public const string ErrorGettingGlobalUsageChart = "Error getting global usage chart";
        public const string ErrorGettingGlobalUsageBreakdown = "Error getting global usage breakdown";
        public const string FailedToResolveWorkspaceNamesTopWorkspaces = "Failed to resolve real workspace names for Top Workspaces";
        public const string ErrorGettingTopWorkspaces = "Error getting top workspaces";
        public const string FailedToResolveWorkspaceNamesAlerts = "Failed to resolve workspace names for alerts";
        public const string ErrorGettingUsageAlerts = "Error getting usage alerts";
        public const string ErrorGettingWorkspaceCredits = "Error getting workspace credits for WorkspaceId {WorkspaceId}";
        public const string ErrorGettingInvoices = "Error getting invoices for WorkspaceId {WorkspaceId}";
        public const string FailedToResolveWorkspaceNamesGlobalInvoices = "Failed to resolve workspace names for global invoices";
        public const string ErrorGettingGlobalInvoices = "Error getting global invoices";
        public const string FailedToCreateCheckoutSession = "Failed to create checkout session";
        public const string FailedToGetCheckoutSession = "Failed to get checkout session";
        public const string FailedToProcessPaymentEvent = "Failed to process payment event";
        public const string ErrorGettingPlans = "Error getting plans";
        public const string ErrorGettingPlanById = "Error getting plan by Id {PlanId}";
        public const string ErrorGettingPlanBySlug = "Error getting plan by Slug {Slug}";
        public const string ErrorCreatingPlan = "Error creating plan";
        public const string ErrorUpdatingPlan = "Error updating plan";
        public const string ErrorDeactivatingPlan = "Error deactivating plan";
        public const string ErrorConsumingCredits = "Error consuming credits for {IdempotencyKey}";
        public const string ErrorFetchingActiveSubscription = "Error fetching active subscription for WorkspaceId {WorkspaceId}";
        public const string ErrorCancellingSubscription = "Error cancelling subscription for WorkspaceId {WorkspaceId}";
        public const string ErrorChangingSubscription = "Error changing subscription for WorkspaceId {WorkspaceId} to PlanId {PlanId}";
        public const string ErrorActivatingSubscription = "Error activating subscription for WorkspaceId {WorkspaceId}";
        public const string ErrorLoggingUsageRecord = "Error logging usage record for workspace {WorkspaceId}";
        public const string FailedToAuthorizeUser = "Failed to authorize user {UserId} for workspace {WorkspaceId}";
        public const string FailedToRetrieveWorkspaceMemberDetails = "Failed to retrieve workspace member details via gRPC for Workspace: {WorkspaceId}, User: {UserId}";
        public const string FailedToSendNotificationsToUsers = "Failed to send notifications to users";
        public const string FailedToSendNotificationViaGrpcToUser = "Failed to send notification via gRPC client to user {UserId}";
        public const string FailedToProcessRedisBillingNotification = "Failed to process incoming Redis billing notification message.";
        public const string ErrorCreatingTrialSubscription = "Error creating trial subscription for WorkspaceId {WorkspaceId}";
    }

    public static class Notifications
    {
        public const string Channel = "warptalk:notifications:new";
        public const string TypePrefix = "billing.";
        public const string ActionCreated = "created";
        public const string ActionCancelled = "cancelled";
        public const string ActionChanged = "changed";
        public const string AllUsers = "all";

        public static class HubEvents
        {
            public const string BillingNotification = "BillingNotification";
        }

        public static class HubGroups
        {
            public const string UserGroupTemplate = "user:{0}";
        }

        public static class HubPaths
        {
            public const string Billing = "/hubs/billing";
        }

        public static class Types
        {
            public const string CreditsUpdated = "billing.credits_updated";
            public const string SubscriptionChanged = "billing.subscription_changed";
            public const string PlanChanged = "billing.plan_changed";
            public const string RateChange = "billing.rate_change";
            public const string OverageStarted = "billing.overage_started";
        }

        public static class MetadataKeys
        {
            public const string ChangedServices = "changed_services";
        }

        public static class ActionUrls
        {
            public const string Billing = "/billing";
        }

        public static class RateChange
        {
            public const string ChangeTemplate = "• {0}: {1:0.##} → {2:0.##} {3}";
            public const string SttLabel = "Speech-to-Text (STT)";
            public const string TranslationLabel = "Real-time Translation";
            public const string TtsLabel = "Text-to-Speech (TTS)";
            public const string VoiceCloneLabel = "Voice Clone TTS";
            public const string AiAssistantInputLabel = "AI Assistant (Input)";
            public const string AiAssistantOutputLabel = "AI Assistant (Output)";

            public const string UnitCreditsPerSec = "credits/sec";
            public const string UnitCreditsPer100Chars = "credits/100chars";
            public const string UnitCreditsPer1kTokens = "credits/1k tokens";
        }

        public static class Titles
        {
            public const string SubscriptionUpdated = "Subscription Updated";
            public const string PlanUpdated = "System Plan Update";
            public const string RatesUpdated = "AI Service Rates Updated";
            public const string OverageStarted = "Workspace Overage Alert";
        }

        public static class Templates
        {
            public const string SubscriptionChangedContent = "Your subscription has been {0} to {1}.";
            public const string PlanChangedContent = "The subscription package '{0}' has been {1}.";
            public const string PlanChangedDetails = " Details: {0}";
            public const string RatesUpdatedBody = "WarpTalk has updated the AI service credit rates that apply to your workspace:\n\n{0}\n\nNew rates are effective immediately for all future sessions.";
            public const string OverageStartedContent = "Your workspace '{0}' has consumed all allocated credits for the current billing cycle and is now using overage credits. Your overage usage will be billed at the end of the cycle.";
        }
    }
    public static class Grpc
    {
        public const string Workspace = "Workspace";
        public const string Plan = "Plan";
        public const string DirectTopUpDisabled = "Direct credit top-up is disabled. Credits can only be granted by verified payment processing.";
        public const string AuthenticationRequired = "Authentication required";
        public const string AccessDenied = "Access denied";
        public const string FailedToFetchTransactionHistory = "Failed to fetch transaction history";
        public const string ProcessPaymentEventFailed = "gRPC ProcessPaymentEvent failed";
        public const string FailedToCreateSubscription = "Failed to create subscription";
        public const string NoActiveSubscription = "No active subscription";
        public const string FailedToCancelSubscription = "Failed to cancel subscription";
        public const string UnknownPlan = "Unknown Plan";
        public const string FailedToConsumeCredits = "Failed to consume credits";
        public const string FailedToFetchCreditHistory = "Failed to fetch credit history";
    }

    public static class Webhook
    {
        public const string ProcessedSuccessfully = "Webhook processed successfully.";
        public const string StripeSignatureHeader = "Stripe-Signature";
    }

    public static class ApiErrorMessages
    {
        public const string BillingWorkspaceAccessDenied = "Access denied. You are not an active member of this workspace.";
        public const string BillingWorkspaceRoleDenied = "Access denied. You must be one of the following roles: {0}";
        public const string BillingWorkspaceAuthError = "An error occurred during workspace authorization.";
        public const string BillingCreditsConsumedInvalid = "Credits consumed must be greater than zero.";
        public const string BillingTransactionNotFound = "Payment transaction not found.";
        public const string BillingSessionInactive = "Session is inactive or expired.";
        public const string BillingSubscriptionInvalid = "Subscription invalid.";
        public const string BillingIdempotencyKeyReused = "Idempotency key was reused with a different request payload.";
        public const string BillingHostSubscriptionNotFound = "No active subscription found for the host workspace.";
        public const string BillingHostInsufficientCredits = "Insufficient credits in the host workspace.";

        public const string BillingAnalyticsReportFailed = "Failed to generate billing report.";
        public const string BillingAnalyticsChartFailed = "Failed to generate chart.";
        public const string BillingAnalyticsAdoptionFailed = "Failed to generate feature adoption.";
        public const string BillingAnalyticsGlobalMetricsFailed = "Failed to generate global metrics.";
        public const string BillingAnalyticsGlobalChartFailed = "Failed to generate global chart.";
        public const string BillingAnalyticsGlobalBreakdownFailed = "Failed to generate global usage breakdown.";
        public const string BillingAnalyticsTopWorkspacesFailed = "Failed to get top workspaces.";

        public const string BillingWorkspaceIdInvalid = "Invalid workspace id";
        public const string BillingCheckoutSessionCreateFailed = "Failed to create checkout session";
        public const string BillingCheckoutSessionGetFailed = "Failed to get checkout session";
        public const string BillingPaymentEventFailed = "Failed to process payment event";
        public const string BillingInvoiceNotFound = "Invoice not found.";
        public const string BillingInvoiceAlreadyPaid = "Invoice is already paid.";

        public const string BillingOwnerEmailInvalid = "Owner email is invalid.";
        public const string BillingTrialAlreadyExistsForOwnerDomain = "Trial already exists for this owner email domain.";
        public const string BillingAiServiceSuspended = "Workspace AI service is suspended.";
        public const string BillingAiServiceNotSuspended = "Workspace AI service is not suspended.";
        public const string BillingAiServiceResumed = "Workspace AI service has been resumed.";
        public const string BillingWorkspaceMeetingQuotaExceeded = "Workspace has exceeded its active meeting quota.";
        public const string BillingContractTermsInvalid = "Subscription contract terms are invalid.";
        public const string BillingContractPriceBelowFloor = "Effective contract price per credit is below the billing floor.";
        public const string BillingContractOverageTermsInvalid = "Subscription overage terms are invalid.";
        public const string BillingCannotReduceCommitmentDuringOverage = "Cannot reduce contract commitment while subscription is in overage.";
    }

    public static class Validation
    {
        public static class Plan
        {
            public const string SlugPattern = "^[a-z0-9]+(?:-[a-z0-9]+)*$";
        }
    }

    public static class ConfigurationSecurity
    {
        public const string LocalPostgresPasswordToken = "Password=" + "postgres";
        public const string PlaceholderToken = "placeholder";
        public const string ChangeMeToken = "CHANGE_ME";
        public const string ProductionPlaceholderSecrets = "Billing service production configuration contains local placeholder secrets.";
    }
}
