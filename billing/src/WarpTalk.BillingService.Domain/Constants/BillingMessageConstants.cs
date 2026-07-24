namespace WarpTalk.BillingService.Domain.Constants;

public static class BillingMessageConstants
{
    public static class SuccessMessages
    {
        public const string CreditsAddedTitle = "Credits Added";
        public const string CreditsAddedContent = "Top-up processed successfully. Added {0} credits.";
        public const string SimulatePaymentMessage = "Payment simulated successfully.";
        public const string SubscriptionActivationTopUp = "Subscription activation top-up";
        public const string StripeCreditTopUp = "Stripe Credit Top-Up";
        public const string SubscriptionPlanActivationTemplate = "Subscription Plan Activation: {0}";
    }

    public static class AdjustmentMessages
    {
        public const string DefaultReason = "Manual credit adjustment";
        public const string AddedTitle = "Credits Added";
        public const string DeductedTitle = "Credits Deducted";
        public const string ContentTemplate = "Admin adjusted credit balance by {0}{1} credits. Reason: {2}";
        public const string ReasonUpgradedDowngraded = "upgraded/downgraded";
        public const string PlanUpgradeDirect = "Plan upgrade to {0} (Stripe Direct)";
        public const string PlanUpgradeSimulation = "Plan upgrade to {0} (Simulation)";
    }

    public static class PlanAuditMessages
    {
        public const string PriceChanged = "Price changed from {0:N0} to {1:N0} {2}";
        public const string CreditsChanged = "Credits per cycle changed from {0:N0} to {1:N0}";
        public const string MaxParticipantsChanged = "Max participants changed from {0} to {1}";

        public const string NameChanged = "Name changed from '{0}' to '{1}'";
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
    }

    public static class Notifications
    {
        public const string Channel = "warptalk:notifications:new";
        public const string ActionCreated = "created";
        public const string ActionCancelled = "cancelled";
        public const string ActionChanged = "changed";

        public static class Types
        {
            public const string CreditsUpdated = "billing.credits_updated";
            public const string SubscriptionChanged = "billing.subscription_changed";
            public const string PlanChanged = "billing.plan_changed";
        }

        public static class Titles
        {
            public const string SubscriptionUpdated = "Subscription Updated";
            public const string PlanUpdated = "System Plan Update";
        }

        public static class Templates
        {
            public const string SubscriptionChangedContent = "Your subscription has been {0} to {1}.";
            public const string PlanChangedContent = "The subscription package '{0}' has been {1}.";
        }
    }
}
