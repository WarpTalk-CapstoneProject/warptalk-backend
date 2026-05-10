namespace WarpTalk.Shared;

/// <summary>
/// Centralized error code constants used across all microservices in Result.Failure() calls.
/// These are compile-time constants (zero runtime overhead).
/// </summary>
public static class ErrorCodes
{
    // ── Common ────────────────────────────────────────────
    public const string NotFound = "NOT_FOUND";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string InvalidState = "INVALID_STATE";
    public const string ValidationError = "VALIDATION_ERROR";

    // ── Auth ──────────────────────────────────────────────
    public const string EmailExists = "EMAIL_EXISTS";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountInactive = "ACCOUNT_INACTIVE";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string UserInactive = "USER_INACTIVE";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string InvalidPassword = "INVALID_PASSWORD";

    // ── TranslationRoom ──────────────────────────────────────────
    public const string TranslationRoomNotActive = "MEETING_NOT_ACTIVE";

    // ── Notification ─────────────────────────────────────
    public const string PreferencesNotFound = "PREFERENCES_NOT_FOUND";

    // ── Billing ───────────────────────────────────────────
    public const string BillingSubscriptionNotFound = "BILLING_SUBSCRIPTION_NOT_FOUND";
    public const string BillingInsufficientCredits = "BILLING_INSUFFICIENT_CREDITS";
    public const string BillingSubscriptionAlreadyActive = "BILLING_SUBSCRIPTION_ALREADY_ACTIVE";
    public const string BillingPlanNotFound = "BILLING_PLAN_NOT_FOUND";
    public const string BillingConcurrencyConflict = "BILLING_CONCURRENCY_CONFLICT";
    public const string BillingValidationFailed = "BILLING_VALIDATION_FAILED";
    public const string BillingServiceUnavailable = "BILLING_SERVICE_UNAVAILABLE";
}
