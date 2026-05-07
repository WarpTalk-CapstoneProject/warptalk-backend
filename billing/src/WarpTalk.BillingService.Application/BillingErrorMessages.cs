namespace WarpTalk.BillingService.Application;

/// <summary>
/// Centralized user-facing messages for Billing API. Use codes from <see cref="BillingErrorCodes"/> as keys.
/// </summary>
public static class BillingErrorMessages
{
    public const string INTERNAL_SERVER_ERROR = "An unexpected error occurred";
    public const string VALIDATION_FAILED = "Validation failed";
    public const string AUTHENTICATION_REQUIRED = "Authentication required";
    public const string ACCESS_DENIED = "Access denied";
    public const string SUBSCRIPTION_NOT_FOUND = "No active subscription found";
    public const string SUBSCRIPTION_ALREADY_ACTIVE = "Workspace already has an active subscription";
    public const string PLAN_NOT_FOUND = "Plan not found";
    public const string INVALID_WORKSPACE_ID = "Workspace ID cannot be empty";
    public const string INVALID_AMOUNT = "Amount must be greater than 0";
    public const string INSUFFICIENT_CREDITS = "Insufficient credits";

    public static string GetMessage(string code)
    {
        return code switch
        {
            BillingErrorCodes.SERVICE_UNAVAILABLE => INTERNAL_SERVER_ERROR,
            BillingErrorCodes.VALIDATION_FAILED => VALIDATION_FAILED,
            BillingErrorCodes.INVALID_WORKSPACE_ID => INVALID_WORKSPACE_ID,
            BillingErrorCodes.PLAN_NOT_FOUND => PLAN_NOT_FOUND,
            BillingErrorCodes.SUBSCRIPTION_NOT_FOUND => SUBSCRIPTION_NOT_FOUND,
            BillingErrorCodes.SUBSCRIPTION_ALREADY_ACTIVE => SUBSCRIPTION_ALREADY_ACTIVE,
            BillingErrorCodes.INVALID_AMOUNT => INVALID_AMOUNT,
            BillingErrorCodes.INSUFFICIENT_CREDITS => INSUFFICIENT_CREDITS,
            BillingErrorCodes.WORKSPACE_UNAUTHORIZED => ACCESS_DENIED,
            _ => INTERNAL_SERVER_ERROR,
        };
    }
}
