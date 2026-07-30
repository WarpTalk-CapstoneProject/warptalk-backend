namespace WarpTalk.BillingService.Application;

/// <summary>
/// Standardized error codes for Billing Service.
/// Format: BILLING_[RESOURCE]_[ACTION]_[ERROR_TYPE]
/// </summary>
public static class BillingErrorCodes
{
    // Plan errors
    public const string PLAN_NOT_FOUND = "BILLING_PLAN_NOT_FOUND";
    public const string PLAN_INACTIVE = "BILLING_PLAN_INACTIVE";
    public const string PLAN_INVALID = "BILLING_PLAN_INVALID";

    // Subscription errors
    public const string SUBSCRIPTION_NOT_FOUND = "BILLING_SUBSCRIPTION_NOT_FOUND";
    public const string SUBSCRIPTION_ALREADY_ACTIVE = "BILLING_SUBSCRIPTION_ALREADY_ACTIVE";
    public const string SUBSCRIPTION_NOT_ACTIVE = "BILLING_SUBSCRIPTION_NOT_ACTIVE";
    public const string SUBSCRIPTION_CONFLICT = "BILLING_SUBSCRIPTION_CONFLICT";
    public const string SUBSCRIPTION_EXPIRED = "BILLING_SUBSCRIPTION_EXPIRED";

    // Credits errors
    public const string INSUFFICIENT_CREDITS = "BILLING_INSUFFICIENT_CREDITS";
    public const string CREDITS_OPERATION_FAILED = "BILLING_CREDITS_OPERATION_FAILED";

    // Workspace errors
    public const string WORKSPACE_NOT_FOUND = "BILLING_WORKSPACE_NOT_FOUND";
    public const string WORKSPACE_UNAUTHORIZED = "BILLING_WORKSPACE_UNAUTHORIZED";

    // Transaction errors
    public const string TRANSACTION_NOT_FOUND = "BILLING_TRANSACTION_NOT_FOUND";
    public const string TRANSACTION_PROCESSING_FAILED = "BILLING_TRANSACTION_PROCESSING_FAILED";
    public const string TRANSACTION_PAYMENT_FAILED = "BILLING_TRANSACTION_PAYMENT_FAILED";

    // Validation errors
    public const string VALIDATION_FAILED = "BILLING_VALIDATION_FAILED";
    public const string INVALID_AMOUNT = "BILLING_INVALID_AMOUNT";
    public const string INVALID_WORKSPACE_ID = "BILLING_INVALID_WORKSPACE_ID";

    // Concurrency errors
    public const string CONCURRENCY_CONFLICT = "BILLING_CONCURRENCY_CONFLICT";

    // Service errors
    public const string SERVICE_UNAVAILABLE = "BILLING_SERVICE_UNAVAILABLE";
    public const string EXTERNAL_SERVICE_ERROR = "BILLING_EXTERNAL_SERVICE_ERROR";
}
