using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.Gateway.Configuration;
using WarpTalk.Gateway.Platform;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The gateway reads four platform settings: maintenance mode (+ message and allowlist), the public
/// status the web polls, and the three live rate limits. Each test drives the real middleware or
/// the real limiter registration, changes the setting mid-test, and proves the running pipeline
/// follows it — no restart, no re-registration.
/// </summary>
public sealed class PlatformSettingsGatewayTests
{
    private const string Ip = "203.0.113.9";

    // ── Maintenance mode ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Maintenance_turns_on_and_off_live_and_carries_the_message()
    {
        var (source, reader) = Settings();
        using var host = await StartAsync(reader);
        var client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await Send(client, "/api/v1/workspaces/mine")).StatusCode);

        source.Set(PlatformSettingsCatalog.MaintenanceEnabled, true)
              .Set(PlatformSettingsCatalog.MaintenanceMessage, "Back at 10:00 UTC.");
        await reader.RefreshAsync();

        var blocked = await Send(client, "/api/v1/workspaces/mine");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal("300", Assert.Single(blocked.Headers.GetValues("Retry-After")));
        var body = await blocked.Content.ReadFromJsonAsync<MaintenanceBody>();
        Assert.Equal(MaintenanceModeMiddleware.ErrorCode, body!.ErrorCode);
        Assert.Equal("Back at 10:00 UTC.", body.Message);

        source.Set(PlatformSettingsCatalog.MaintenanceEnabled, false);
        await reader.RefreshAsync();
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "/api/v1/workspaces/mine")).StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/api/v1/platform/status")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/admin/settings")]
    public async Task Maintenance_leaves_health_status_sign_in_and_the_admin_api_open(string path)
    {
        var (source, reader) = Settings();
        source.Set(PlatformSettingsCatalog.MaintenanceEnabled, true);
        await reader.RefreshAsync();
        using var host = await StartAsync(reader);

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, (await Send(host.GetTestClient(), path)).StatusCode);
    }

    [Fact]
    public async Task Maintenance_lets_allowlisted_emails_through_case_insensitively()
    {
        var (source, reader) = Settings();
        source.Set(PlatformSettingsCatalog.MaintenanceEnabled, true)
              .Set(PlatformSettingsCatalog.MaintenanceAllowlist, new[] { "ops@warptalk.vn" });
        await reader.RefreshAsync();
        using var host = await StartAsync(reader);
        var client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await Send(client, "/api/v1/workspaces/mine", email: "OPS@warptalk.vn")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(client, "/api/v1/workspaces/mine", email: "someone@else.vn")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(client, "/api/v1/workspaces/mine")).StatusCode);
    }

    [Fact]
    public async Task Public_status_reports_the_live_values_and_never_the_allowlist()
    {
        var (source, reader) = Settings();
        using var host = await StartAsync(reader);
        var client = host.GetTestClient();

        var before = await client.GetFromJsonAsync<PlatformStatusEndpoints.PlatformStatus>(PlatformStatusEndpoints.Path);
        Assert.False(before!.Maintenance.Enabled);
        Assert.Null(before.Maintenance.Message);
        Assert.True(before.GoogleSignInEnabled);
        Assert.Equal("support@warptalk.vn", before.SupportEmail);

        source.Set(PlatformSettingsCatalog.MaintenanceEnabled, true)
              .Set(PlatformSettingsCatalog.MaintenanceAllowlist, new[] { "ops@warptalk.vn" })
              .Set(PlatformSettingsCatalog.SupportEmail, "help@warptalk.vn")
              .Set(PlatformSettingsCatalog.GoogleSignInEnabled, false);
        await reader.RefreshAsync();

        var raw = await client.GetStringAsync(PlatformStatusEndpoints.Path);
        Assert.DoesNotContain("ops@warptalk.vn", raw, StringComparison.OrdinalIgnoreCase);
        var after = await client.GetFromJsonAsync<PlatformStatusEndpoints.PlatformStatus>(PlatformStatusEndpoints.Path);
        Assert.True(after!.Maintenance.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(after.Maintenance.Message));
        Assert.False(after.GoogleSignInEnabled);
        Assert.Equal("help@warptalk.vn", after.SupportEmail);
    }

    // ── Live rate limits ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_limit_follows_the_live_setting()
    {
        var (source, reader) = Settings();
        source.Set(PlatformSettingsCatalog.LoginRateLimit, 2);
        await reader.RefreshAsync();
        using var host = await StartAsync(reader, withRateLimiter: true);
        var client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await Send(client, "/api/v1/auth/login", HttpMethod.Post)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "/api/v1/auth/login", HttpMethod.Post)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(client, "/api/v1/auth/login", HttpMethod.Post)).StatusCode);

        // Raised by an operator: the same IP gets the new budget without a restart.
        source.Set(PlatformSettingsCatalog.LoginRateLimit, 4);
        await reader.RefreshAsync();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(client, "/api/v1/auth/login", HttpMethod.Post)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(client, "/api/v1/auth/login", HttpMethod.Post)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_and_user_limits_follow_the_live_settings()
    {
        var (source, reader) = Settings();
        source.Set(PlatformSettingsCatalog.IpRateLimit, 30).Set(PlatformSettingsCatalog.UserRateLimit, 31);
        await reader.RefreshAsync();
        using var host = await StartAsync(reader, withRateLimiter: true);
        var client = host.GetTestClient();

        for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.OK, (await Send(client, "/api/v1/workspaces/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(client, "/api/v1/workspaces/mine")).StatusCode);

        for (var i = 0; i < 31; i++) Assert.Equal(HttpStatusCode.OK, (await Send(client, "/api/v1/workspaces/mine", userId: "u-1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(client, "/api/v1/workspaces/mine", userId: "u-1")).StatusCode);
    }

    [Fact]
    public void Without_a_stored_value_the_configured_limit_applies()
    {
        var (_, reader) = Settings();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IPlatformSettings>(reader).BuildServiceProvider(),
        };

        Assert.Equal(7, GatewayRateLimiterExtensions.LivePermitLimit(context, PlatformSettingsCatalog.LoginRateLimit, 7));
        Assert.Equal(7, GatewayRateLimiterExtensions.LivePermitLimit(new DefaultHttpContext(), PlatformSettingsCatalog.LoginRateLimit, 7));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────

    private static (InMemoryPlatformSettingsSource Source, PlatformSettingsReader Reader) Settings()
    {
        var source = new InMemoryPlatformSettingsSource();
        return (source, new PlatformSettingsReader(source, NullLogger<PlatformSettingsReader>.Instance, cacheTtl: TimeSpan.FromMinutes(5)));
    }

    private static Task<HttpResponseMessage> Send(
        HttpClient client, string path, HttpMethod? method = null, string? email = null, string? userId = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Add("X-Test-Client-Ip", Ip);
        if (email is not null) request.Headers.Add("X-Test-Email", email);
        if (userId is not null) request.Headers.Add("X-Test-User-Id", userId);
        return client.SendAsync(request);
    }

    private static async Task<IHost> StartAsync(PlatformSettingsReader reader, bool withRateLimiter = false)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["RateLimits:IpPermitLimit"] = "10000",
            ["RateLimits:UserPermitLimit"] = "10000",
            ["RateLimits:LoginPermitLimit"] = "10000",
            ["RateLimits:WindowSeconds"] = "60",
        };

        return await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(configuration));
                webHost.ConfigureServices((context, services) =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddSingleton<IPlatformSettings>(reader);
                    services.AddWarpTalkGatewayRateLimiting(context.Configuration);
                });
                webHost.Configure(app =>
                {
                    // Stands in for authentication: the identity the middleware and limiter read.
                    app.Use(async (httpContext, next) =>
                    {
                        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(
                            httpContext.Request.Headers["X-Test-Client-Ip"].FirstOrDefault() ?? Ip);
                        var claims = new List<Claim>();
                        if (httpContext.Request.Headers["X-Test-User-Id"].FirstOrDefault() is { } userId)
                            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
                        if (httpContext.Request.Headers["X-Test-Email"].FirstOrDefault() is { } email)
                        {
                            claims.Add(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
                            claims.Add(new Claim("email", email));
                        }

                        if (claims.Count > 0)
                            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
                        await next(httpContext);
                    });

                    app.UseMiddleware<MaintenanceModeMiddleware>();
                    app.UseRouting();
                    if (withRateLimiter) app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/api/v1/auth/login", () => Results.Ok())
                            .RequireRateLimiting(GatewayRateLimiterExtensions.LoginPolicyName);
                        endpoints.MapGet("/api/v1/auth/login", () => Results.Ok());
                        endpoints.MapGet("/api/v1/workspaces/mine", () => Results.Ok());
                        endpoints.MapGet("/api/v1/admin/settings", () => Results.Ok());
                        endpoints.MapGet("/health/live", () => Results.Ok());
                        endpoints.MapPlatformStatus();
                    });
                });
            })
            .StartAsync();
    }

    private sealed record MaintenanceBody(string? Error, string? ErrorCode, string? Message);
}
