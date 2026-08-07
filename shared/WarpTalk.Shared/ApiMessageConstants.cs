namespace WarpTalk.Shared;

public static class ApiMessageConstants
{
    public static class ErrorMessages
    {
        // Common API ProblemDetails Titles & Details
        public const string ValidationFailedTitle = "Validation Failed";
        public const string UnauthorizedTokenDetail = "Could not extract a valid user ID from the authentication token.";

        // Billing
        public const string BillingInternalError = "An unexpected error occurred.";
        public const string BillingSubscriptionNotFound = "No active subscription found.";
        public const string BillingSubscriptionAlreadyActive = "Workspace already has an active subscription.";
        public const string BillingPlanNotFound = "Plan not found.";
        public const string BillingInvalidWorkspaceId = "Workspace ID cannot be empty.";
        public const string BillingInvalidAmount = "Amount must be greater than 0.";
        public const string BillingInsufficientCredits = "Insufficient credits.";
        public const string BillingWorkspaceNotFound = "Workspace not found.";
        public const string BillingConcurrencyConflict = "A conflict occurred. Please retry.";
        public const string BillingAccessDenied = "Access denied.";
        public const string BillingValidationFailed = "Validation failed.";
        public const string BillingPaymentNotFound = "Payment not found.";
        public const string BillingInvalidOrderCode = "Invalid OrderCode.";
        public const string BillingAutoRenewNotSupported = "Auto-renew is not supported at this time.";
        public const string BillingWorkspaceIdNotInSessionMetadata = "Workspace ID not found in session metadata.";
        public const string BillingAccessDeniedOwnerAdminRequired = "Access denied. You must be an Owner or Admin of the workspace associated with this session.";
        public const string BillingStripeWebhookFailed = "Failed to process webhook event.";
        public const string BillingDuplicatePlanSlug = "A plan with this slug already exists.";
        public const string BillingSimulationInvalidClientRef = "Invalid or missing client_reference_id.";
        public const string BillingSimulationInvalidEvent = "Simulation only supports a paid checkout.session.completed event for now.";
        public const string BillingSimulationFailed = "Failed to process simulated payment.";
        public const string BillingStripeUpdateFailed = "Failed to update subscription on Stripe. Please check your payment method and try again.";
        public const string BillingTopUpMinimumRequired = "Top-up amount must be at least {0} credits to meet Stripe's minimum payment requirement.";
        public const string BillingDirectTopUpDisabled = "Direct credit top-up is disabled. Use Stripe Checkout and a verified payment webhook to grant credits.";
    }

    public static class ValidationMessages
    {
        public const string TitleRequired = "Title is required.";
        public const string TitleMaxLength = "Title cannot exceed 255 characters.";
        public const string WorkspaceRequired = "Workspace is required.";

        // Auth & Profiles
        public const string EmailRequired = "Email is required.";
        public const string EmailInvalidFormat = "Email must be a valid @gmail.com address.";
        public const string PasswordRequired = "Password is required.";
        public const string PasswordMinLength = "Password must be at least 6 characters long.";
        public const string FullNameRequired = "Full name is required.";
        public const string FullNameNotEmpty = "Full name cannot be empty.";
        public const string RefreshTokenRequired = "Refresh token is required.";
        public const string GoogleIdTokenRequired = "Google ID token is required.";
        public const string PreferredLanguageInvalid = "Preferred language format is invalid.";
        public const string TimezoneInvalid = "Timezone must be a valid IANA identifier.";
        public const string NewPasswordRequired = "New password is required.";
        public const string NewPasswordMinLength = "New password must be at least 6 characters long.";

        // User Settings
        public const string FontSizeOutOfBounds = "Font size must be between {0} and {1}.";
        public const string MaxParticipantsOutOfBounds = "Default max participants must be between {0} and {1}.";
        public const string InvalidTheme = "Invalid theme. Supported: {0}, {1}, {2}.";
        public const string InvalidRoomType = "Invalid translation room type.";
        public const string InvalidSpeakLanguage = "Invalid default speak language format.";
        public const string InvalidListenLanguage = "Invalid default listen language format.";

        // Billing
        public const string WorkspaceIdRequired = "WorkspaceId is required.";
        public const string WorkspaceIdMismatch = "Workspace ID in URL does not match request body.";
        public const string AmountGreaterThanZero = "Amount must be greater than 0.";
        public const string PlanIdRequired = "Plan ID is required.";
        public const string ReferenceTypeRequired = "ReferenceType is required.";
        public const string PageSizeOutOfBounds = "Page size must be between 1 and 200.";
        public const string PageNumberOutOfBounds = "Page number must be >= 1.";

        public const string PlanNameRequired = "Plan name is required.";
        public const string PlanNameMaxLength = "Plan name must not exceed 100 characters.";
        public const string PlanSlugRequired = "Slug is required.";
        public const string PlanSlugMaxLength = "Slug must not exceed 50 characters.";
        public const string PlanSlugInvalid = "Slug must be lowercase alphanumeric characters and hyphens only (e.g., 'gold-tier').";
        public const string PlanTierRequired = "Tier is required.";
        public const string PlanTierMaxLength = "Tier must not exceed 20 characters.";
        public const string PlanCurrencyRequired = "Currency is required.";
        public const string PlanCurrencyInvalid = "Currency must be 'USD' or 'VND'.";
        public const string PlanBillingCycleRequired = "Billing cycle is required.";
        public const string PlanBillingCycleInvalid = "Billing cycle must be 'monthly', 'semiannual', or 'yearly'.";
        public const string PlanMinPrice = "Price for {0} must be at least {1} due to Stripe payment constraints.";
        public const string PlanCreditsPerCycleInvalid = "Credits per cycle must be greater than 0.";
        public const string PlanMaxParticipantsInvalid = "Max participants must be at least 2.";
        public const string PlanMaxLanguagesInvalid = "Max languages must be within the configured plan limit.";
        public const string PlanSortOrderInvalid = "Sort order must be non-negative.";
        public const string PlanFeaturesInvalid = "Features must be a valid JSON string.";
        public const string PlanOverageCapInvalid = "Overage cap credits must be non-negative.";
        public const string PlanOverageCapTooHigh = "Overage cap credits must not exceed credits per cycle.";
        public const string PlanOveragePriceInvalid = "Overage price per credit must be non-negative.";
        public const string PlanOveragePriceRequired = "Overage price per credit must be greater than 0 when overage is enabled.";
        public const string PlanLowBalanceThresholdInvalid = "Low balance threshold must be greater than overage cap when overage is enabled.";
        public const string PlanLowBalanceThresholdTooHigh = "Low balance threshold must be lower than credits per cycle.";
        public const string PlanRolloverCapInvalid = "Rollover cap credits must be non-negative.";
        public const string PlanRolloverCapTooHigh = "Rollover cap credits must not exceed credits per cycle.";
        public const string PlanEffectivePriceFloorInvalid = "Effective price per credit is below the billing floor.";
        public const string PlanInvoiceTermsInvalid = "Invoice terms days must be greater than 0.";
        public const string PlanInvoiceGraceInvalid = "Invoice grace hours must be greater than 0.";
    }
}
