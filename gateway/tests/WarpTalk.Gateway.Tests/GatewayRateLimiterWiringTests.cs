using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WarpTalk.Gateway.Configuration;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// WT-327. The gateway hardcoded PermitLimit = 100/minute/IP with no RejectionStatusCode, no
/// OnRejected and no logging, so ASP.NET answered throttled callers with its default 503 and an
/// empty body. Measured on production: 77 consecutive `503 content-length: 0` with no Retry-After,
/// which is genuinely indistinguishable from a crashed service — it was misdiagnosed as an outage
/// twice in one day. Meanwhile docker-compose has always passed RateLimits__IpPermitLimit=300 and
/// nothing ever read it, so production believed it was running 300 and was running 100.
/// </summary>
public sealed class GatewayRateLimiterWiringTests
{
    /// <summary>
    /// The regression that caused the incident: production's configured 300 must actually be the
    /// number the limiter enforces. 300 requests are admitted and the 301st is not — proving the
    /// value came from configuration and not from the old literal 100.
    /// </summary>
    [Fact]
    public void ConfiguredIpPermitLimit_IsTheLimitActuallyEnforced()
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:IpPermitLimit"] = "300"
        });

        var context = RequestFrom("203.0.113.10", "/api/v1/workspaces");

        for (var i = 1; i <= 300; i++)
        {
            using var lease = limiter.AttemptAcquire(context);
            Assert.True(lease.IsAcquired, $"Request {i} of the configured 300 should have been admitted.");
        }

        using var rejected = limiter.AttemptAcquire(context);
        Assert.False(rejected.IsAcquired);
    }

    /// <summary>
    /// Environment-variable form, exactly as docker-compose supplies it
    /// (RateLimits__IpPermitLimit=300). This is the binding that was missing.
    /// </summary>
    [Fact]
    public void EnvironmentVariableForm_BindsToOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits__IpPermitLimit".Replace("__", ":")] = "300"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWarpTalkGatewayRateLimiting(configuration);

        var bound = services.BuildServiceProvider()
            .GetRequiredService<IOptions<GatewayRateLimitOptions>>().Value;

        Assert.Equal(300, bound.IpPermitLimit);
    }

    /// <summary>
    /// Absent configuration the default is 300, not the 100 that was hardcoded.
    /// </summary>
    [Fact]
    public void WithoutConfiguration_DefaultIsThreeHundredNotOneHundred()
    {
        Assert.Equal(300, new GatewayRateLimitOptions().IpPermitLimit);
    }

    /// <summary>
    /// 429, not 503. This single line is the difference between "please slow down" and the whole
    /// team believing production has crashed.
    /// </summary>
    [Fact]
    public void RejectionStatusCode_Is429NotDefault503()
    {
        var options = new RateLimiterOptions();
        GatewayRateLimiterExtensions.Configure(options, new GatewayRateLimitOptions());

        Assert.Equal(StatusCodes.Status429TooManyRequests, options.RejectionStatusCode);
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, options.RejectionStatusCode);
    }

    /// <summary>
    /// A rejection must carry Retry-After and a body the client can act on — the production
    /// rejections had neither.
    /// </summary>
    [Fact]
    public async Task Rejection_SetsRetryAfterAndAnActionableBody()
    {
        var options = new RateLimiterOptions();
        var limits = new GatewayRateLimitOptions { IpPermitLimit = 1, WindowSeconds = 60 };
        GatewayRateLimiterExtensions.Configure(options, limits);

        var context = RequestFrom("203.0.113.11", "/api/v1/workspaces");
        var body = new MemoryStream();
        context.Response.Body = body;

        // Exhaust the single permit, then capture the lease the middleware would hand to OnRejected.
        using var admitted = options.GlobalLimiter!.AttemptAcquire(context);
        Assert.True(admitted.IsAcquired);
        using var refused = options.GlobalLimiter.AttemptAcquire(context);
        Assert.False(refused.IsAcquired);

        await options.OnRejected!(
            new OnRejectedContext { HttpContext = context, Lease = refused },
            CancellationToken.None);

        var retryAfter = context.Response.Headers.RetryAfter.ToString();
        Assert.False(string.IsNullOrWhiteSpace(retryAfter));
        Assert.True(int.Parse(retryAfter) > 0);

        var payload = Encoding.UTF8.GetString(body.ToArray());
        Assert.False(string.IsNullOrWhiteSpace(payload));

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            root.GetProperty("status").GetInt32());
        Assert.Contains("Rate limit", root.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.True(root.GetProperty("retryAfterSeconds").GetInt32() > 0);
    }

    /// <summary>
    /// Health probes must never be throttled. MapHealthChecks is mapped after UseRateLimiter in
    /// Program.cs, so without this exemption an orchestrator reading a throttled /health/live as
    /// "unhealthy" would kill a gateway that is serving perfectly well — throttling turning itself
    /// into a real outage.
    /// </summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public void HealthProbes_AreNeverThrottled(string path)
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:IpPermitLimit"] = "1"
        });

        var context = RequestFrom("203.0.113.12", path);

        for (var i = 0; i < 50; i++)
        {
            using var lease = limiter.AttemptAcquire(context);
            Assert.True(lease.IsAcquired, $"Health probe {path} was throttled on request {i + 1}.");
        }
    }

    /// <summary>An ordinary path on the same limiter still throttles — the exemption is scoped.</summary>
    [Fact]
    public void NonHealthPath_IsStillThrottled()
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:IpPermitLimit"] = "1"
        });

        var context = RequestFrom("203.0.113.13", "/api/v1/workspaces");

        using var first = limiter.AttemptAcquire(context);
        Assert.True(first.IsAcquired);
        using var second = limiter.AttemptAcquire(context);
        Assert.False(second.IsAcquired);
    }

    /// <summary>
    /// The defence-room case. IP was the only partition, so everyone behind one NAT shared a single
    /// budget: the presenter's laptop, the projector machine and three examiners on the same wifi
    /// were one partition, and the first person to exhaust it broke the app for the rest. Two
    /// signed-in users from the SAME address must now have independent budgets.
    /// </summary>
    [Fact]
    public void TwoUsersBehindOneNat_DoNotShareOneBudget()
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:UserPermitLimit"] = "2"
        });

        const string sharedIp = "203.0.113.20";
        var presenter = AuthenticatedRequestFrom(sharedIp, "/api/v1/workspaces", "user-presenter");
        var examiner = AuthenticatedRequestFrom(sharedIp, "/api/v1/workspaces", "user-examiner");

        for (var i = 0; i < 2; i++)
        {
            using var lease = limiter.AttemptAcquire(presenter);
            Assert.True(lease.IsAcquired);
        }

        using var presenterRefused = limiter.AttemptAcquire(presenter);
        Assert.False(presenterRefused.IsAcquired, "The presenter should have spent their own budget.");

        // The examiner shares the presenter's IP and nothing else. Before the fix this was already
        // refused, because the budget was the address rather than the person.
        using var examinerAdmitted = limiter.AttemptAcquire(examiner);
        Assert.True(
            examinerAdmitted.IsAcquired,
            "A second signed-in user on the same address must not inherit the first user's exhausted budget.");
    }

    /// <summary>
    /// One user is still limited — per-user partitioning must not become no partitioning. Also
    /// pins that a signed-in caller is measured against UserPermitLimit, not IpPermitLimit.
    /// </summary>
    [Fact]
    public void AnAuthenticatedCaller_IsHeldToUserPermitLimit()
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:UserPermitLimit"] = "3",
            ["RateLimits:IpPermitLimit"] = "300"
        });

        var context = AuthenticatedRequestFrom("203.0.113.21", "/api/v1/workspaces", "user-solo");

        for (var i = 1; i <= 3; i++)
        {
            using var lease = limiter.AttemptAcquire(context);
            Assert.True(lease.IsAcquired, $"Request {i} of the configured 3 should have been admitted.");
        }

        using var rejected = limiter.AttemptAcquire(context);
        Assert.False(rejected.IsAcquired);
    }

    /// <summary>
    /// Anonymous traffic keeps the IP budget. An unauthenticated flood has no other identity, so
    /// this is where an address-shaped limit belongs — and it must not silently become per-request.
    /// </summary>
    [Fact]
    public void AnonymousCallers_StillShareTheIpBudget()
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:IpPermitLimit"] = "2",
            ["RateLimits:UserPermitLimit"] = "180"
        });

        const string sharedIp = "203.0.113.22";
        var first = RequestFrom(sharedIp, "/api/v1/auth/register");
        var second = RequestFrom(sharedIp, "/api/v1/workspaces");

        using var one = limiter.AttemptAcquire(first);
        Assert.True(one.IsAcquired);
        using var two = limiter.AttemptAcquire(second);
        Assert.True(two.IsAcquired);

        using var three = limiter.AttemptAcquire(first);
        Assert.False(three.IsAcquired, "Two anonymous requests from one address share one budget.");
    }

    /// <summary>
    /// A user id must not be confused with an address. Two anonymous callers from DIFFERENT
    /// addresses stay in different partitions, as they always did.
    /// </summary>
    [Fact]
    public void AnonymousCallersFromDifferentAddresses_KeepSeparateBudgets()
    {
        var limiter = BuildGlobalLimiter(new Dictionary<string, string?>
        {
            ["RateLimits:IpPermitLimit"] = "1"
        });

        using var first = limiter.AttemptAcquire(RequestFrom("203.0.113.23", "/api/v1/workspaces"));
        Assert.True(first.IsAcquired);
        using var second = limiter.AttemptAcquire(RequestFrom("203.0.113.24", "/api/v1/workspaces"));
        Assert.True(second.IsAcquired);
    }

    private static DefaultHttpContext AuthenticatedRequestFrom(string ip, string path, string userId)
    {
        var context = RequestFrom(ip, path);
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId)],
                authenticationType: "TestAuth"));
        return context;
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
        context.Request.Method = "GET";
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        return context;
    }
}
