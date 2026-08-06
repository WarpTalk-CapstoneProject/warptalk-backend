using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using WarpTalk.Gateway.Services;

namespace WarpTalk.Gateway.Configuration;

/// <summary>
/// Registers the gateway's rate limiter from <see cref="GatewayRateLimitOptions"/>.
///
/// This lives outside Program.cs on purpose. The limits used to be compile-time constants
/// inside the AddRateLimiter callback, which meant the RateLimits__* environment variables
/// that docker-compose has always passed did nothing at all (WT-327): production believed it
/// was running 300 requests/minute per IP and was actually running 100. Registration that can
/// be built from a plain IConfiguration is registration a test can hold to its configured value.
/// </summary>
public static class GatewayRateLimiterExtensions
{
    /// <summary>Requests under this prefix are never throttled. See <see cref="IsHealthProbe"/>.</summary>
    public const string HealthProbePrefix = "/health";

    public const string LoginPolicyName = "LoginPolicy";
    public const string InboxPolicyName = "InboxPolicy";

    private const string RejectionLogCategory = "WarpTalk.Gateway.RateLimiting";

    public static IServiceCollection AddWarpTalkGatewayRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GatewayRateLimitOptions.SectionName);
        var limits = section.Get<GatewayRateLimitOptions>() ?? new GatewayRateLimitOptions();
        limits.Validate();

        // Bound as an option too, so anything else that needs the numbers reads the same source.
        services.Configure<GatewayRateLimitOptions>(section);
        services.AddRateLimiter(options => Configure(options, limits));

        return services;
    }

    /// <summary>
    /// Shapes <see cref="RateLimiterOptions"/> from already-resolved limits. Public so the test
    /// suite can assert the wiring without standing up the whole gateway host.
    /// </summary>
    public static void Configure(RateLimiterOptions options, GatewayRateLimitOptions limits)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);

        var window = TimeSpan.FromSeconds(limits.WindowSeconds);

        // 429, not ASP.NET's default 503. A 503 tells a client the server is broken and sends
        // whoever is on call hunting a phantom outage; a 429 tells the client to slow down.
        // WT-327 was misdiagnosed as an outage for exactly this reason.
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            // Liveness and readiness must answer even when the caller's IP is saturated.
            // An orchestrator that reads a throttled /health/live as "unhealthy" will kill a
            // gateway that is serving perfectly well — which turns throttling into a real outage.
            // Handled in the partition rather than by moving MapHealthChecks above
            // UseRateLimiter, so the exemption cannot be lost to a later pipeline reshuffle.
            if (IsHealthProbe(httpContext.Request.Path))
            {
                return RateLimitPartition.GetNoLimiter<string>(HealthProbePrefix);
            }

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: RequestRateLimitPartitionKeys.Ip(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = limits.IpPermitLimit,
                    Window = window
                });
        });

        options.AddFixedWindowLimiter(LoginPolicyName, opt =>
        {
            opt.PermitLimit = limits.LoginPermitLimit;
            opt.Window = window;
        });

        options.AddFixedWindowLimiter(InboxPolicyName, opt =>
        {
            opt.PermitLimit = limits.InboxPermitLimit;
            opt.Window = window;
        });

        options.OnRejected = async (context, cancellationToken) =>
        {
            var retryAfterSeconds = (int)Math.Ceiling(ResolveRetryAfter(context.Lease, window).TotalSeconds);
            var partitionKey = RequestRateLimitPartitionKeys.Ip(context.HttpContext);

            // Rejections used to be completely silent — no status override, no header, no log —
            // so throttling was indistinguishable from a dead gateway. One line naming the
            // partition and the path is the difference between a five-minute diagnosis and a day.
            context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(RejectionLogCategory)
                .LogWarning(
                    "Rate limit rejected {Method} {Path} for partition {PartitionKey}. "
                    + "Responding {StatusCode} with Retry-After {RetryAfterSeconds}s.",
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path.Value,
                    partitionKey,
                    StatusCodes.Status429TooManyRequests,
                    retryAfterSeconds);

            if (context.HttpContext.Response.HasStarted)
            {
                return;
            }

            context.HttpContext.Response.Headers.RetryAfter =
                retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            // An empty body is what made this look like an outage in the browser's network tab.
            // Give the client something it can branch on and show a human.
            await context.HttpContext.Response.WriteAsJsonAsync(
                new
                {
                    type = "https://tools.ietf.org/html/rfc6585#section-4",
                    title = "Too Many Requests",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = $"Rate limit exceeded. Retry after {retryAfterSeconds} seconds.",
                    retryAfterSeconds
                },
                options: null,
                contentType: "application/problem+json",
                cancellationToken);
        };
    }

    public static bool IsHealthProbe(PathString path) =>
        path.StartsWithSegments(HealthProbePrefix, StringComparison.OrdinalIgnoreCase);

    private static TimeSpan ResolveRetryAfter(RateLimitLease lease, TimeSpan window)
    {
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata) && metadata > TimeSpan.Zero)
        {
            return metadata;
        }

        return window;
    }
}
