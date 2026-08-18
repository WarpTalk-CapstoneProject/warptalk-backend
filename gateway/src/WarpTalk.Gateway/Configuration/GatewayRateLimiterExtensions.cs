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

            // Signing out and refreshing get their own budget, because they are how a browser
            // ESCAPES a bad session — and they are unauthenticated by construction, so without
            // this they draw on the general anonymous IP pool below, shared with every other
            // tokenless request from that address.
            //
            // WT-405: that pool spilled in production and took POST /api/v1/auth/logout with it.
            // The gateway answered 429, so AuthSessionCookies.Clear never ran; the browser had
            // already torn its own session down and been told it succeeded, while the server
            // kept the HttpOnly cookies and a refresh-token family live for another seven days.
            // The user was signed out in the tab and signed in on the server, and the natural
            // recovery — reloading — spent more permits from the same exhausted pool, so the
            // state could not clear itself. Refresh sits here for the same reason: a client that
            // cannot refresh and cannot sign out has no move left.
            //
            // Not GetNoLimiter: /auth/refresh takes a credential, and an unbounded credential
            // endpoint is a brute-force surface. A separate bounded partition is the point —
            // ordinary browsing can no longer starve these two, and they cannot starve it.
            // (/auth/login stays on LoginPolicy's 5/min per IP; it is not a recovery path.)
            if (IsSessionRecovery(httpContext.Request.Path))
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"session-recovery:{RequestRateLimitPartitionKeys.Ip(httpContext)}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = limits.SessionRecoveryPermitLimit,
                        Window = window
                    });
            }

            // A signed-in caller is partitioned by WHO THEY ARE, not by where they are sitting.
            //
            // IP was the only partition, and the partition key is what decides who shares a budget.
            // Everyone behind one NAT — an office, a lecture theatre, a defence room with the
            // presenter's laptop, the projector machine and three examiners on the same wifi — was
            // ONE partition. A single WarpTalk navigation is roughly ten gateway requests
            // (/workspaces, its settings, members, documents, two room lists, notifications,
            // assistant skills, presence, plus the SignalR negotiate), so the whole room shared
            // about thirty page views a minute, and the first person to exhaust it broke the app
            // for everybody else on that address. The natural recovery — reloading — spends ten
            // more permits and makes it worse.
            //
            // Per user, that room is five independent budgets instead of one shared one, and
            // UserPermitLimit alone (180/min ≈ 18 navigations a minute, sustained) is more headroom
            // per person than the shared IP limit could ever give them. Anonymous traffic still
            // partitions by IP on IpPermitLimit, which is where an IP budget belongs: it is the
            // only identity an unauthenticated flood has.
            //
            // Requires UseRateLimiter to run after UseAuthentication — see Program.cs, where the
            // ordering is pinned with a comment. Before authentication, HttpContext.User is empty
            // and every request would silently fall back to the anonymous IP partition.
            var userKey = RequestRateLimitPartitionKeys.User(httpContext);
            var isAuthenticated = httpContext.User.Identity?.IsAuthenticated == true
                && !userKey.StartsWith(RequestRateLimitPartitionKeys.AnonymousPrefix, StringComparison.Ordinal);

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: isAuthenticated
                    ? $"user:{userKey}"
                    : $"ip:{RequestRateLimitPartitionKeys.Ip(httpContext)}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = isAuthenticated ? limits.UserPermitLimit : limits.IpPermitLimit,
                    Window = window
                });
        });

        // AddFixedWindowLimiter(policyName, ...) registers an UN-partitioned limiter: one bucket
        // for the entire platform, not one per caller. Both of these policies were registered
        // that way, which inverted what they were for. LoginPolicy defaults to 5 permits per
        // minute, so a single client sending six login attempts a minute 429'd login for every
        // user of the product — a denial of service that costs the attacker nothing, and a live
        // risk during a demo. It also meant per-attacker brute-force throttling was not per
        // attacker: everyone's attempts drained the same shared bucket.
        //
        // Partitioned by IP, which is the only caller identity available on the login route:
        // the request is by definition unauthenticated, so there is no user or workspace claim
        // to key on. (RequestRateLimitPartitionKeys.Workspace falls back to the X-Workspace-Id
        // request header, which a caller sets freely — never partition a limiter on that.)
        options.AddPolicy(LoginPolicyName, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: RequestRateLimitPartitionKeys.Ip(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = limits.LoginPermitLimit,
                    Window = window
                }));

        // The inbox route is authenticated, so the caller's own identity is the honest key here;
        // it falls back to "anonymous:<ip>" when no subject claim is present.
        options.AddPolicy(InboxPolicyName, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: RequestRateLimitPartitionKeys.User(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = limits.InboxPermitLimit,
                    Window = window
                }));

        options.OnRejected = async (context, cancellationToken) =>
        {
            var retryAfterSeconds = (int)Math.Ceiling(ResolveRetryAfter(context.Lease, window).TotalSeconds);
            // An earlier revision logged the client IP here, on the grounds that the rejecting
            // limiter's own partition key is not exposed on OnRejectedContext. That stopped being
            // the honest field once the global limiter began keying signed-in callers by user id:
            // several people behind one venue NAT share an IP and no longer share a budget, so an
            // IP in this line would point at the wrong thing exactly when it matters. ResolvePartitionKey
            // reproduces the same choice the limiter made.
            var partitionKey = ResolvePartitionKey(context.HttpContext);

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

    /// <summary>
    /// The same key the global limiter partitioned on, so a rejection log names the budget that was
    /// actually exhausted. Logging the IP for a user-partitioned rejection would send whoever reads
    /// it looking at the wrong thing.
    /// </summary>
    private static string ResolvePartitionKey(HttpContext context)
    {
        // Session-recovery FIRST, because the limiter checks it first. This branch was missing:
        // the partition was added to the limiter and not to the line that reports it, so every
        // refused logout logged `ip:` — the budget it was NOT drawn from.
        //
        // That is not a cosmetic slip. Diagnosing 289 refused logouts in three hours meant
        // deciding whether the session-recovery budget was exhausted or the guard was never
        // matching, and this line answered "ip:" to both. A log that names the wrong budget is
        // worse than one that names none, because it is believed.
        if (IsSessionRecovery(context.Request.Path))
        {
            return $"session-recovery:{RequestRateLimitPartitionKeys.Ip(context)}";
        }

        var userKey = RequestRateLimitPartitionKeys.User(context);

        return context.User.Identity?.IsAuthenticated == true
            && !userKey.StartsWith(RequestRateLimitPartitionKeys.AnonymousPrefix, StringComparison.Ordinal)
            ? $"user:{userKey}"
            : $"ip:{RequestRateLimitPartitionKeys.Ip(context)}";
    }

    public static bool IsHealthProbe(PathString path) =>
        path.StartsWithSegments(HealthProbePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The two endpoints a browser needs in order to get OUT of a broken session.
    ///
    /// Exact paths, not a prefix over /api/v1/auth: everything else under that prefix is
    /// ordinary API surface, and /auth/login in particular must keep LoginPolicy's much
    /// tighter per-IP budget. A prefix match here would quietly raise the login limit from
    /// five a minute to sixty.
    /// </summary>
    public static bool IsSessionRecovery(PathString path) =>
        path.Equals("/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/v1/auth/refresh", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan ResolveRetryAfter(RateLimitLease lease, TimeSpan window)
    {
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata) && metadata > TimeSpan.Zero)
        {
            return metadata;
        }

        return window;
    }
}
