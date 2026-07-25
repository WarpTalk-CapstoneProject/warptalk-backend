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
    public const string BillingPlanNotFound = "BILLING_PLAN_NOT_FOUND";
    public const string BillingInsufficientCredits = "BILLING_INSUFFICIENT_CREDITS";

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
