using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WarpTalk.Gateway.Configuration;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The login limiter was registered with <c>options.AddFixedWindowLimiter(LoginPolicyName, …)</c>,
/// which creates a single UN-partitioned bucket shared by the entire platform — unlike the
/// GlobalLimiter directly above it, which correctly partitions by IP. With the default of 5
/// permits per minute, one client sending six login attempts a minute locked every user of the
/// product out of logging in. Trivial denial of service, and a live risk during the capstone
/// defence.
///
/// These run real HTTP requests through the real <c>UseRateLimiter</c> middleware and the real
/// <see cref="GatewayRateLimiterExtensions.Configure"/> registration. Asserting against
/// <c>RateLimiterOptions</c> alone cannot show what a *second* client receives, which is the
/// entire property under test.
/// </summary>
public sealed class LoginRateLimitPartitioningTests
{
    private const string AttackerIp = "203.0.113.50";
    private const string BystanderIp = "198.51.100.77";

    /// <summary>
    /// The defect, stated as a test: one client burning the login limit must not affect anybody
    /// else. Before the fix the bystander got 429 on their very first attempt.
    /// </summary>
    [Fact]
    public async Task One_client_exhausting_the_login_limit_does_not_lock_out_everyone_else()
    {
        using var host = await StartGatewayPipelineAsync(loginPermitLimit: 5);
        var client = host.GetTestClient();

        // Attacker burns the whole login window from a single address.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var admitted = await PostLoginAsync(client, AttackerIp);
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        var throttled = await PostLoginAsync(client, AttackerIp);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // A completely unrelated user must still be able to log in.
        var bystander = await PostLoginAsync(client, BystanderIp);
        Assert.Equal(HttpStatusCode.OK, bystander.StatusCode);
    }

    /// <summary>
    /// And the throttling still has to work per client — partitioning must not become "no limit".
    /// </summary>
    [Fact]
    public async Task Each_client_still_gets_throttled_within_its_own_partition()
    {
        using var host = await StartGatewayPipelineAsync(loginPermitLimit: 2);
        var client = host.GetTestClient();

        foreach (var ip in new[] { AttackerIp, BystanderIp })
        {
            Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, ip)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, ip)).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(client, ip)).StatusCode);
        }
    }

    /// <summary>
    /// The inbox policy carried the identical un-partitioned registration. It is authenticated,
    /// so it is keyed on the caller rather than their address.
    /// </summary>
    [Fact]
    public async Task Inbox_limit_is_partitioned_per_user_not_shared_platform_wide()
    {
        using var host = await StartGatewayPipelineAsync(inboxPermitLimit: 3);
        var client = host.GetTestClient();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetInboxAsync(client, "user-a")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetInboxAsync(client, "user-a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetInboxAsync(client, "user-b")).StatusCode);
    }

    /// <summary>
    /// A prior release (WT-327) replaced ASP.NET's default 503-with-empty-body rejection with a
    /// 429 carrying Retry-After and application/problem+json. Partitioning the policy must not
    /// have cost that: this asserts it over real HTTP, on a rejection from the named login policy
    /// rather than the global limiter.
    /// </summary>
    [Fact]
    public async Task Login_rejection_is_429_with_retry_after_and_a_problem_json_body()
    {
        using var host = await StartGatewayPipelineAsync(loginPermitLimit: 1);
        var client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, AttackerIp)).StatusCode);

        var rejected = await PostLoginAsync(client, AttackerIp);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);

        Assert.True(rejected.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Equal("60", Assert.Single(retryAfter!));

        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);

        var problem = await rejected.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.NotNull(problem);
        Assert.Equal(429, problem!.Status);
        Assert.Equal("Too Many Requests", problem.Title);
        Assert.Equal(60, problem.RetryAfterSeconds);
    }

    /// <summary>
    /// Health probes must stay exempt. An orchestrator reading a throttled /health/live as
    /// "unhealthy" turns rate limiting into a real outage.
    /// </summary>
    [Fact]
    public async Task Health_probes_are_never_throttled()
    {
        using var host = await StartGatewayPipelineAsync(ipPermitLimit: 1);
        var client = host.GetTestClient();

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var response = await SendAsync(client, HttpMethod.Get, "/health/live", AttackerIp);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string ip) =>
        SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", ip);

    private static Task<HttpResponseMessage> GetInboxAsync(HttpClient client, string userId) =>
        SendAsync(client, HttpMethod.Get, "/api/v1/notifications/inbox", AttackerIp, userId);

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string ip,
        string? userId = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestClientIpHeader, ip);
        if (userId is not null)
        {
            request.Headers.Add(TestUserHeader, userId);
        }

        return client.SendAsync(request);
    }

    private const string TestClientIpHeader = "X-Test-Client-Ip";
    private const string TestUserHeader = "X-Test-User-Id";

    /// <summary>
    /// Stands up a minimal host that uses the gateway's own rate-limiter registration and the
    /// real UseRateLimiter middleware. Only the identity of the caller is faked, because a
    /// TestServer gives every request the same (null) remote address and no authenticated user.
    /// </summary>
    private static async Task<IHost> StartGatewayPipelineAsync(
        int? loginPermitLimit = null,
        int? inboxPermitLimit = null,
        int? ipPermitLimit = null)
    {
        var settings = new Dictionary<string, string?>
        {
            // Kept high so the global IP limiter never masks what the named policies do.
            ["RateLimits:IpPermitLimit"] = (ipPermitLimit ?? 10_000).ToString(),
            ["RateLimits:WindowSeconds"] = "60"
        };

        if (loginPermitLimit is not null)
        {
            settings["RateLimits:LoginPermitLimit"] = loginPermitLimit.Value.ToString();
        }

        if (inboxPermitLimit is not null)
        {
            settings["RateLimits:InboxPermitLimit"] = inboxPermitLimit.Value.ToString();
        }

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(settings));
                webHost.ConfigureServices((context, services) =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddWarpTalkGatewayRateLimiting(context.Configuration);
                });
                webHost.Configure(app =>
                {
                    // TestServer has no real connection or authentication, so the caller identity
                    // the limiter partitions on is supplied per request. This runs BEFORE
                    // UseRateLimiter so the partitioner sees it exactly as it would in production.
                    app.Use(async (httpContext, next) =>
                    {
                        var ip = httpContext.Request.Headers[TestClientIpHeader].FirstOrDefault();
                        if (ip is not null)
                        {
                            httpContext.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                        }

                        var userId = httpContext.Request.Headers[TestUserHeader].FirstOrDefault();
                        if (userId is not null)
                        {
                            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                                new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
                                authenticationType: "Test"));
                        }

                        await next(httpContext);
                    });

                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/api/v1/auth/login", () => Results.Ok())
                            .RequireRateLimiting(GatewayRateLimiterExtensions.LoginPolicyName);

                        endpoints.MapGet("/api/v1/notifications/inbox", () => Results.Ok())
                            .RequireRateLimiting(GatewayRateLimiterExtensions.InboxPolicyName);

                        endpoints.MapGet("/health/live", () => Results.Ok());
                    });
                });
            })
            .StartAsync();

        return host;
    }

    private sealed record ProblemBody(
        string? Type,
        string? Title,
        int Status,
        string? Detail,
        int RetryAfterSeconds);
}
