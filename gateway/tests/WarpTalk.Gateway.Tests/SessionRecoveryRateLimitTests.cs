using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using WarpTalk.Gateway.Configuration;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// WT-405 — the two endpoints a browser uses to ESCAPE a broken session must not be
/// throttleable by ordinary browsing.
///
/// Production, 15 Aug: a sign-out succeeded at 04:05:49, the anonymous IP pool spilled twelve
/// seconds later, and at 04:08:04 the gateway answered POST /api/v1/auth/logout with 429. That
/// request never reached the auth service, so AuthSessionCookies.Clear never ran — the browser
/// had already torn its own session down and reported success, while the server kept all three
/// HttpOnly cookies and a live refresh-token family. Signed out in the tab, signed in on the
/// server, and reloading only spent more permits from the same exhausted pool.
///
/// The cause is partitioning, not volume: /auth/logout and /auth/refresh carry no usable
/// identity, so they fell into the general anonymous IP budget shared with every other tokenless
/// request from that address. These pin them into a partition of their own.
/// </summary>
public sealed class SessionRecoveryRateLimitTests
{
    /// <summary>
    /// The incident itself. An address that has exhausted its anonymous budget must still be
    /// able to sign out — before the fix this lease was refused, which is what stranded the
    /// session.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/auth/logout")]
    [InlineData("/api/v1/auth/refresh")]
    public void AnExhaustedAnonymousBudget_DoesNotBlockSigningOutOrRefreshing(string recoveryPath)
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:IpPermitLimit"] = "2"
        });

        const string sharedIp = "203.0.113.40";

        // Drain the anonymous pool exactly as a browser full of tokenless refetches would.
        for (var i = 0; i < 2; i++)
        {
            using var lease = limiter.AttemptAcquire(RequestFrom(sharedIp, "/api/v1/workspaces"));
            Assert.True(lease.IsAcquired);
        }

        using var exhausted = limiter.AttemptAcquire(RequestFrom(sharedIp, "/api/v1/workspaces"));
        Assert.False(exhausted.IsAcquired, "The anonymous pool should be spent by now.");

        using var recovery = limiter.AttemptAcquire(RequestFrom(sharedIp, recoveryPath));
        Assert.True(
            recovery.IsAcquired,
            $"{recoveryPath} was refused because ordinary traffic had spent the shared budget. "
            + "That is the 429 that left a signed-out browser holding a live server session.");
    }

    /// <summary>
    /// And the converse: recovery traffic must not be able to starve ordinary browsing either.
    /// A separate partition has to be separate in both directions, or this just moves the
    /// problem.
    /// </summary>
    [Fact]
    public void SpendingTheRecoveryBudget_DoesNotStarveOrdinaryTraffic()
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:SessionRecoveryPermitLimit"] = "2",
            ["RateLimits:IpPermitLimit"] = "5"
        });

        const string sharedIp = "203.0.113.41";

        for (var i = 0; i < 2; i++)
        {
            using var lease = limiter.AttemptAcquire(RequestFrom(sharedIp, "/api/v1/auth/refresh"));
            Assert.True(lease.IsAcquired);
        }

        using var refused = limiter.AttemptAcquire(RequestFrom(sharedIp, "/api/v1/auth/refresh"));
        Assert.False(refused.IsAcquired, "Recovery is bounded, not unlimited.");

        using var ordinary = limiter.AttemptAcquire(RequestFrom(sharedIp, "/api/v1/workspaces"));
        Assert.True(ordinary.IsAcquired, "Ordinary browsing must not pay for the recovery budget.");
    }

    /// <summary>
    /// Bounded, not exempt. /auth/refresh takes a credential from the cookie jar, so
    /// GetNoLimiter would turn it into an unmetered brute-force surface — the opposite mistake
    /// to the one being fixed.
    /// </summary>
    [Fact]
    public void RecoveryIsBounded_NotUnlimited()
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:SessionRecoveryPermitLimit"] = "3"
        });

        var context = RequestFrom("203.0.113.42", "/api/v1/auth/refresh");

        for (var i = 1; i <= 3; i++)
        {
            using var lease = limiter.AttemptAcquire(context);
            Assert.True(lease.IsAcquired, $"Request {i} of the configured 3 should have been admitted.");
        }

        using var rejected = limiter.AttemptAcquire(context);
        Assert.False(rejected.IsAcquired);
    }

    /// <summary>
    /// The exemption is scoped to two exact paths. A prefix match over /api/v1/auth would quietly
    /// raise login from LoginPolicy's five a minute to the recovery budget, which is the one
    /// change here that would actually weaken the product.
    /// </summary>
    [Fact]
    public void SignInIsNotARecoveryPath()
    {
        Assert.False(GatewayRateLimiterExtensions.IsSessionRecovery("/api/v1/auth/login"));
        Assert.False(GatewayRateLimiterExtensions.IsSessionRecovery("/api/v1/auth/register"));
        Assert.False(GatewayRateLimiterExtensions.IsSessionRecovery("/api/v1/auth/me"));
        Assert.True(GatewayRateLimiterExtensions.IsSessionRecovery("/api/v1/auth/logout"));
        Assert.True(GatewayRateLimiterExtensions.IsSessionRecovery("/api/v1/auth/refresh"));
    }

    /// <summary>
    /// Two addresses must not share a recovery budget — otherwise one noisy network could deny
    /// sign-out to everybody, which is the same class of failure one layer along.
    /// </summary>
    [Fact]
    public void RecoveryBudgetsArePerAddress()
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:SessionRecoveryPermitLimit"] = "1"
        });

        using var first = limiter.AttemptAcquire(RequestFrom("203.0.113.43", "/api/v1/auth/logout"));
        Assert.True(first.IsAcquired);
        using var second = limiter.AttemptAcquire(RequestFrom("203.0.113.44", "/api/v1/auth/logout"));
        Assert.True(second.IsAcquired);
    }

    /// <summary>Configuration reaches the recovery limiter, like every other limit here.</summary>
    [Fact]
    public void DefaultRecoveryLimit_IsSixtyAMinute()
    {
        Assert.Equal(60, new GatewayRateLimitOptions().SessionRecoveryPermitLimit);
    }

    private static PartitionedRateLimiter<HttpContext> BuildGlobalLimiter(
        Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var limits = configuration.GetSection(GatewayRateLimitOptions.SectionName)
            .Get<GatewayRateLimitOptions>() ?? new GatewayRateLimitOptions();

        var options = new RateLimiterOptions();
        GatewayRateLimiterExtensions.Configure(options, limits);
        return options.GlobalLimiter!;
    }

    private static DefaultHttpContext RequestFrom(string ip, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        context.Request.Path = path;
        return context;
    }
}
