using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The admin Providers page's paths reach billing through the gateway's REAL routing table.
///
/// WHY (same class as WT-445)
///     The Providers API shipped with only "/api/v1/admin/providers/{**catch-all}", and the page
///     loads GET /api/v1/admin/providers — the bare path — first. WT-445 recorded that a catch-all
///     template does not match its bare prefix, and AdminRouteExposureTests only compares Path
///     strings, so nothing could say which it was. These tests load the gateway's own ReverseProxy
///     section into YARP and ask where a request lands and what billing receives, so template,
///     Order and transform semantics are YARP's, not a re-implementation of them.
///
///     Measured with them (2026-09-25): YARP DOES match the bare path with the catch-all route
///     (catch-all parameters are optional) and forwards it to billing unchanged. The explicit root
///     route is therefore belt-and-braces, following admin-meetings-root-route and staff; what
///     these tests add is that every path the web client calls is now proven to reach billing,
///     whichever route carries it.
/// </summary>
public class AdminProvidersRoutingTests
{
    private const string RouteHeader = "X-Test-Route";
    private const string ClusterHeader = "X-Test-Cluster";

    public static TheoryData<string> ProviderPaths() => new()
    {
        // Every path the web client calls (warptalk-web src/lib/api/endpoints.ts adminProviders).
        "/api/v1/admin/providers",
        "/api/v1/admin/providers?tz=Asia%2FHo_Chi_Minh",
        "/api/v1/admin/providers/openai/series?from=2026-09-01&to=2026-09-25&tz=UTC&granularity=day",
        "/api/v1/admin/providers/cartesia/breakdown?by=workspace",
        "/api/v1/admin/providers/livekit/uptime?days=90",
    };

    [Theory]
    [MemberData(nameof(ProviderPaths))]
    public async Task EveryProvidersPathResolvesToBilling(string path)
    {
        using var host = await StartRoutingHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(path);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {path} matched no gateway route (status {(int)response.StatusCode}); the proxy "
            + "would answer 404 and billing would never see it.");
        Assert.Equal("billing-cluster", Header(response, ClusterHeader));
    }

    [Fact]
    public async Task TheBarePathHasItsOwnRoute()
    {
        using var host = await StartRoutingHostAsync();
        using var client = host.GetTestClient();

        using var bare = await client.GetAsync("/api/v1/admin/providers");
        using var nested = await client.GetAsync("/api/v1/admin/providers/openai/series");

        Assert.Equal("admin-providers-root-route", Header(bare, RouteHeader));
        Assert.Equal("admin-providers-route", Header(nested, RouteHeader));
    }

    /// <summary>
    /// What billing actually receives: the request goes through YARP's forwarder and transforms, and
    /// the "upstream" echoes the URL it was sent. Both shapes must arrive at billing's own
    /// controller paths (api/v1/admin/providers and api/v1/admin/providers/{key}/series).
    /// </summary>
    [Theory]
    [InlineData("/api/v1/admin/providers?tz=UTC", "/api/v1/admin/providers?tz=UTC")]
    [InlineData("/api/v1/admin/providers/openai/series?granularity=day", "/api/v1/admin/providers/openai/series?granularity=day")]
    public async Task BillingReceivesItsOwnControllerPath(string path, string upstream)
    {
        using var host = await StartRoutingHostAsync(forward: true);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("localhost:5107", Header(response, "X-Upstream-Host"));
        Assert.Equal(upstream, Header(response, "X-Upstream-Path"));
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    /// <summary>
    /// A host with the gateway's ReverseProxy configuration and nothing else. The proxy pipeline is
    /// replaced by a terminal step that reports the matched route and cluster, so no request leaves
    /// the process. The policies the routes name are registered permissively: this test is about
    /// WHERE a request goes, not whether a caller may send it (AdminRouteExposureTests and the
    /// services' own policies cover that).
    /// </summary>
    private static async Task<IHost> StartRoutingHostAsync(bool forward = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(AppSettingsPath(), optional: false)
            .Build();

        return await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication();
                    services.AddAuthorization(options =>
                        options.AddPolicy("RequireAuth", policy => policy.RequireAssertion(_ => true)));
                    services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin()));
                    services.AddRateLimiter(options =>
                    {
                        foreach (var name in new[] { "LoginPolicy", "InboxPolicy" })
                        {
                            options.AddPolicy(name, _ => RateLimitPartition.GetNoLimiter("test"));
                        }
                    });
                    services.AddReverseProxy().LoadFromConfig(configuration.GetSection("ReverseProxy"));
                    if (forward) services.AddSingleton<IForwarderHttpClientFactory, EchoUpstreamFactory>();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseCors();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseRateLimiter();
                    if (forward)
                    {
                        app.UseEndpoints(endpoints => endpoints.MapReverseProxy());
                        return;
                    }

                    app.UseEndpoints(endpoints => endpoints.MapReverseProxy(pipeline =>
                        pipeline.Use((HttpContext context, Func<Task> _) =>
                        {
                            var proxy = context.GetReverseProxyFeature();
                            context.Response.Headers[RouteHeader] = proxy.Route.Config.RouteId;
                            context.Response.Headers[ClusterHeader] = proxy.Cluster?.Config.ClusterId ?? string.Empty;
                            context.Response.StatusCode = StatusCodes.Status200OK;
                            return Task.CompletedTask;
                        })));
                });
            })
            .StartAsync();
    }

    /// <summary>An upstream that answers every forwarded request with the URL it arrived at.</summary>
    private sealed class EchoUpstreamFactory : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new EchoHandler());

        private sealed class EchoHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
                response.Headers.Add("X-Upstream-Host", request.RequestUri!.Authority);
                response.Headers.Add("X-Upstream-Path", request.RequestUri.PathAndQuery);
                return Task.FromResult(response);
            }
        }
    }

    private static string AppSettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "WarpTalk.Gateway", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        // The build copies the gateway's appsettings.json next to the test assembly as well.
        return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }
}
