namespace WarpTalk.BillingService.API.Utilities;

/// <summary>
/// Helper class for common validation operations across API controllers.
/// </summary>
public static class ValidationHelper
{
    /// <summary>
    /// Validates pagination parameters and enforces safe bounds.
    /// </summary>
    public static (int page, int pageSize) ValidatePagination(int page, int pageSize, int maxPageSize = 100)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > maxPageSize) pageSize = maxPageSize;

        return (page, pageSize);
    }

    /// <summary>
    /// Validates that a GUID is not empty.
    /// </summary>
    public static bool IsValidGuid(Guid id) => id != Guid.Empty;

    /// <summary>
    /// Validates that a decimal amount is positive.
    /// </summary>
    public static bool IsValidAmount(decimal amount) => amount > 0;

    /// <summary>
    /// Validates that a string is not null or whitespace.
    /// </summary>
    public static bool IsValidString(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Masks sensitive data for logging (shows first 4 and last 4 characters).
    /// </summary>
    public static string MaskSensitiveData(string value, int visibleChars = 4)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= visibleChars * 2)
            return "***";

        var first = value.Substring(0, visibleChars);
        var last = value.Substring(value.Length - visibleChars);
        return $"{first}...{last}";
    }

    /// <summary>
    /// Masks GUID for logging.
    /// </summary>
    public static string MaskGuid(Guid id) => MaskSensitiveData(id.ToString());
}
