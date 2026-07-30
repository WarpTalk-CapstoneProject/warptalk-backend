namespace WarpTalk.BillingService.API.Common;

/// <summary>
/// Standardized error response for API endpoints
/// </summary>
public class ApiErrorResponse
{
    /// <summary>Error code (e.g., BILLING_SUBSCRIPTION_NOT_FOUND)</summary>
    public string Code { get; set; }

    /// <summary>Human-readable error message</summary>
    public string Message { get; set; }

    /// <summary>Additional error details</summary>
    public string? Details { get; set; }

    /// <summary>Timestamp when error occurred</summary>
    public DateTime Timestamp { get; set; }

    public ApiErrorResponse(string message, string code, string? details = null)
    {
        Message = message;
        Code = code;
        Details = details;
        Timestamp = DateTime.UtcNow;
    }
}
