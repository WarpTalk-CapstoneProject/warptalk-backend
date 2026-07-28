namespace WarpTalk.Shared;

/// <summary>
/// Centralized error code constants used across all microservices in Result.Failure() calls.
/// These are compile-time constants (zero runtime overhead).
/// </summary>
public static class ErrorCodes
{
    // ── Common ────────────────────────────────────────────
    public const string InternalServerError = "INTERNAL_SERVER_ERROR";
    public const string NotFound = "NOT_FOUND";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string InvalidState = "INVALID_STATE";
    public const string ValidationError = "VALIDATION_ERROR";
    public const string Conflict = "CONFLICT";

    // ── Billing ───────────────────────────────────────────
    public const string BillingSubscriptionNotFound = "BILLING_SUBSCRIPTION_NOT_FOUND";
    public const string BillingSubscriptionAlreadyActive = "BILLING_SUBSCRIPTION_ALREADY_ACTIVE";
    public const string BillingSubscriptionNotActive = "BILLING_SUBSCRIPTION_NOT_ACTIVE";
    public const string BillingSubscriptionConflict = "BILLING_SUBSCRIPTION_CONFLICT";
    public const string BillingSubscriptionExpired = "BILLING_SUBSCRIPTION_EXPIRED";
    public const string BillingPlanNotFound = "BILLING_PLAN_NOT_FOUND";
    public const string BillingPlanInactive = "BILLING_PLAN_INACTIVE";
    public const string BillingPaymentInvalidStatus = "BILLING_PAYMENT_INVALID_STATUS";
    public const string BillingSimulationInvalidRequest = "BILLING_SIMULATION_INVALID_REQUEST";
    public const string BillingSimulationFailed = "BILLING_SIMULATION_FAILED";

    public const string BillingPlanInvalid = "BILLING_PLAN_INVALID";
    public const string BillingDuplicatePlanSlug = "BILLING_DUPLICATE_PLAN_SLUG";
    public const string BillingInsufficientCredits = "BILLING_INSUFFICIENT_CREDITS";
    public const string BillingCreditsOperationFailed = "BILLING_CREDITS_OPERATION_FAILED";
    public const string BillingWorkspaceNotFound = "BILLING_WORKSPACE_NOT_FOUND";
    public const string BillingWorkspaceUnauthorized = "BILLING_WORKSPACE_UNAUTHORIZED";
    public const string BillingTransactionNotFound = "BILLING_TRANSACTION_NOT_FOUND";
    public const string BillingTransactionProcessingFailed = "BILLING_TRANSACTION_PROCESSING_FAILED";
    public const string BillingTransactionPaymentFailed = "BILLING_TRANSACTION_PAYMENT_FAILED";
    public const string BillingValidationFailed = "BILLING_VALIDATION_FAILED";
    public const string BillingInvalidAmount = "BILLING_INVALID_AMOUNT";
    public const string BillingInvalidWorkspaceId = "BILLING_INVALID_WORKSPACE_ID";
    public const string BillingConcurrencyConflict = "BILLING_CONCURRENCY_CONFLICT";
    public const string BillingServiceUnavailable = "BILLING_SERVICE_UNAVAILABLE";
    public const string BillingExternalServiceError = "BILLING_EXTERNAL_SERVICE_ERROR";

    // ── Auth ──────────────────────────────────────────────
    public const string EmailExists = "EMAIL_EXISTS";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountInactive = "ACCOUNT_INACTIVE";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string UserInactive = "USER_INACTIVE";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string InvalidPassword = "INVALID_PASSWORD";
    public const string AccountPending = "ACCOUNT_PENDING";
    public const string CooldownActive = "COOLDOWN_ACTIVE";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string EmailNotVerified = "EMAIL_NOT_VERIFIED";
    public const string MinAuthMethodRequired = "MIN_AUTH_METHOD_REQUIRED";

    // ── TranslationRoom ──────────────────────────────────────────
    public const string TranslationRoomNotActive = "MEETING_NOT_ACTIVE";

    // ── Notification ─────────────────────────────────────
    public const string PreferencesNotFound = "PREFERENCES_NOT_FOUND";
}
