using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace WarpTalk.BillingService.API.Security;

/// <summary>
/// Extension methods for configuring rate limiting policies
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Rate limiting policy names
    /// </summary>
    public static class Policies
    {
        public const string PaymentEndpoints = "PaymentEndpoints";
        public const string QuotaEndpoints = "QuotaEndpoints";
        public const string WebhookEndpoints = "WebhookEndpoints";
        public const string AdminEndpoints = "AdminEndpoints";
        public const string HealthEndpoints = "HealthEndpoints";
    }

    /// <summary>
    /// Configures rate limiting policies for billing service
    /// </summary>
    public static RateLimiterOptions ConfigureBillingRateLimiting(this RateLimiterOptions options)
    {
        // Global policy: IP-based, 100 requests per minute
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true
                }));

        // Policy 1: Strict limit for checkout/payment endpoints (5 req/min)
        options.AddPolicy(Policies.PaymentEndpoints, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true
                }));

        // Policy 2: Moderate limit for quota operations (20 req/min)
        options.AddPolicy(Policies.QuotaEndpoints, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true
                }));

        // Policy 3: Webhooks - higher limit (50 req/min)
        options.AddPolicy(Policies.WebhookEndpoints, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 50,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true
                }));

        // Policy 4: Admin endpoints - very high limit (500 req/min)
        options.AddPolicy(Policies.AdminEndpoints, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 500,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true
                }));

        // Policy 5: Health checks - very high limit (10000 req/min - effectively unlimited)
        options.AddPolicy(Policies.HealthEndpoints, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10000,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true
                }));

        // On rejected, return 429 Too Many Requests
        options.OnRejected = async (context, _) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                message = "Too many requests. Please try again later.",
                retryAfter = context.HttpContext.Response.Headers["Retry-After"]
            });
        };

        return options;
    }
}
