namespace WarpTalk.BillingService.API.Middleware;

/// <summary>
/// Middleware to log requests with TraceId for audit and debugging.
/// </summary>
public class SensitiveDataMaskingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SensitiveDataMaskingMiddleware> _logger;

    public SensitiveDataMaskingMiddleware(RequestDelegate next, ILogger<SensitiveDataMaskingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Log request with masked sensitive data
        LogRequestWithMasking(context);

        await _next(context);

        // Log response status with TraceId
        _logger.LogInformation(
            "Response: StatusCode={StatusCode}. TraceId={TraceId}",
            context.Response.StatusCode,
            context.TraceIdentifier);
    }

    private void LogRequestWithMasking(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path;
        var queryString = MaskSensitiveDataInString(context.Request.QueryString.ToString());

        _logger.LogInformation(
            "Request: {Method} {Path}{QueryString}. TraceId={TraceId}",
            method,
            path,
            queryString,
            context.TraceIdentifier);
    }

    /// <summary>
    /// Masks sensitive data in a string by replacing sensitive patterns with asterisks.
    /// </summary>
    private static string MaskSensitiveDataInString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Simple pattern masking - avoid complex regex to prevent escaping issues
        var result = input;

        // Mask common payment/sensitive identifiers
        result = MaskPatternSimple(result, "payosId=", 4);
        result = MaskPatternSimple(result, "amount=", 4);
        result = MaskPatternSimple(result, "AmountVnd=", 4);
        result = MaskPatternSimple(result, "PayOsTransactionId=", 4);

        return result;
    }

    /// <summary>
    /// Simple pattern masking by finding a key and masking the value after it.
    /// </summary>
    private static string MaskPatternSimple(string input, string key, int visibleChars = 4)
    {
        var index = input.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return input;

        var startOfValue = index + key.Length;
        var endOfValue = input.IndexOfAny(new[] { '&', ',', '}', ' ', '"', '\'' }, startOfValue);

        if (endOfValue < 0)
            endOfValue = input.Length;

        var value = input.Substring(startOfValue, endOfValue - startOfValue);
        var masked = MaskValue(value, visibleChars);

        var prefix = input.Substring(0, startOfValue);
        var suffix = input.Substring(endOfValue);

        return prefix + masked + suffix;
    }

    private static string MaskValue(string value, int visibleChars = 4)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= visibleChars * 2)
            return "***";

        var first = value.Substring(0, Math.Min(visibleChars, value.Length / 2));
        var last = value.Substring(Math.Max(0, value.Length - visibleChars));
        return $"{first}...{last}";
    }
}
