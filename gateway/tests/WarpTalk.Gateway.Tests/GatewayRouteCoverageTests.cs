using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// Every API prefix the product calls has a gateway route. WT-425.
///
/// THE BUG THIS EXISTS FOR
///     RoomArtifactsController has lived at api/v1/room-artifacts for as long as post-meeting
///     artifacts have existed, and the gateway had no route for that prefix. Every download of a
///     transcript or an AI summary was answered 404 by the reverse proxy and never reached
///     TranslationRoomService at all.
///
///     From outside it looked like missing data: the Meeting record listed both outputs as Ready
///     — that list travels over /api/v1/translation-rooms, which IS routed — and opening one
///     failed. So the same artifact row was simultaneously present and absent depending on which
///     prefix asked for it, and the only visible symptom was a bare "Request failed with status
///     code 404".
///
/// WHY A LIST AND NOT REFLECTION
///     The gateway cannot see the other services' controllers; they are separate assemblies in
///     separate deployables, which is exactly why the omission was possible. A hand-kept list is
///     the honest shape: adding a prefix here is the moment somebody remembers the gateway has to
///     learn about it too, and the failure message says so.
/// </summary>
public class GatewayRouteCoverageTests
{
    /// <summary>
    /// Prefixes the web client calls. Each must resolve to a route in appsettings.json.
    ///
    /// Not exhaustive over every endpoint — that would be a second copy of the routing table.
    /// It covers the prefixes whose absence is silent, meaning the UI shows a plausible screen
    /// and one control 404s.
    /// </summary>
    public static TheoryData<string> RoutedPrefixes() => new()
    {
        "/api/v1/auth/",
        "/api/v1/translation-rooms/",
        "/api/v1/translation-room-series/",
        // The one that was missing. Post-meeting artifact download and consent.
        "/api/v1/room-artifacts/",
        "/api/v1/transcripts/",
        "/api/v1/workspaces/",
        "/api/v1/meetings/",
        "/api/v1/notifications/",
        "/api/v1/glossaries/",
        "/api/v1/assistant/",
        "/api/v1/billing/",
        "/api/v1/subscriptions/",
        "/api/v1/plans/",
        "/api/v1/credits/",
        "/api/v1/invoices/",
        "/api/v1/payments/",
    };

    [Theory]
    [MemberData(nameof(RoutedPrefixes))]
    public void EveryPrefixTheProductCallsHasAGatewayRoute(string prefix)
    {
        var paths = RouteMatchPaths();

        Assert.True(
            paths.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)),
            $"No gateway route matches {prefix}. Requests to it are answered 404 by the reverse "
            + "proxy and never reach the service, which from the outside looks like missing data "
            + "rather than missing routing. Add a route to appsettings.json.");
    }

    /// <summary>
    /// Every route forwards to a cluster that exists.
    ///
    /// A ClusterId typo is the same failure one step later: the route matches, and the proxy has
    /// nowhere to send it.
    /// </summary>
    [Fact]
    public void EveryRouteNamesAClusterThatExists()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()));
        var proxy = document.RootElement.GetProperty("ReverseProxy");

        var clusters = proxy.GetProperty("Clusters")
            .EnumerateObject()
            .Select(cluster => cluster.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var route in proxy.GetProperty("Routes").EnumerateObject())
        {
            var clusterId = route.Value.GetProperty("ClusterId").GetString();
            Assert.True(
                clusterId is not null && clusters.Contains(clusterId),
                $"Route {route.Name} forwards to cluster '{clusterId}', which is not defined.");
        }
    }

    /// <summary>
    /// A route's transform must rewrite to the same prefix it matched.
    ///
    /// The routes here are pass-through: the services mount the identical paths. A transform that
    /// drifted from its match would forward to a path the service does not serve — a 404 from the
    /// far side, which reads exactly like the one this class is about.
    /// </summary>
    [Fact]
    public void EveryRouteForwardsToThePathItMatched()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()));

        foreach (var route in document.RootElement
                     .GetProperty("ReverseProxy").GetProperty("Routes").EnumerateObject())
        {
            if (!route.Value.TryGetProperty("Transforms", out var transforms)) continue;

            var matchPath = route.Value.GetProperty("Match").GetProperty("Path").GetString();
            foreach (var transform in transforms.EnumerateArray())
            {
                if (!transform.TryGetProperty("PathPattern", out var pattern)) continue;
                Assert.Equal(matchPath, pattern.GetString());
            }
        }
    }

    private static List<string> RouteMatchPaths()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()));

        return document.RootElement
            .GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .EnumerateObject()
            .Select(route => route.Value.GetProperty("Match").GetProperty("Path").GetString() ?? "")
            .ToList();
    }

    /// <summary>Walks up from the test binary to the gateway project beside it.</summary>
    private static string AppSettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "WarpTalk.Gateway", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find the gateway's appsettings.json from " + AppContext.BaseDirectory);
    }
}
