using System.Text.Json;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The gateway must expose only the admin routes the portal actually needs (WT-205). A
/// catch-all such as /api/v1/admin/{**catch-all} would publish every future admin endpoint the
/// moment a service defines one, before anyone had decided it should be reachable.
/// </summary>
public sealed class AdminRouteExposureTests
{
    private static readonly string[] ApprovedAdminRoutePaths =
    [
        "/api/v1/admin/notifications/{**catch-all}",
        "/api/v1/admin/global-glossary/{**catch-all}",
        "/api/v1/admin/workspaces/{**catch-all}",
    ];

    private static JsonElement Routes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
            .GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .Clone();
    }

    private static List<string> AdminRoutePaths() =>
        Routes()
            .EnumerateObject()
            .Select(route => route.Value.GetProperty("Match").GetProperty("Path").GetString()!)
            .Where(path => path.StartsWith("/api/v1/admin/", StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void OnlyApprovedAdminRoutesAreExposed()
    {
        var actual = AdminRoutePaths();

        Assert.Equal(
            ApprovedAdminRoutePaths.OrderBy(path => path, StringComparer.Ordinal),
            actual.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void NoRouteCatchesTheWholeAdminNamespace()
    {
        var paths = Routes()
            .EnumerateObject()
            .Select(route => route.Value.GetProperty("Match").GetProperty("Path").GetString()!)
            .ToList();

        Assert.DoesNotContain(paths, path =>
            path.Equals("/api/v1/admin/{**catch-all}", StringComparison.Ordinal)
            || path.Equals("/api/v1/{**catch-all}", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryAdminRouteTargetsAKnownCluster()
    {
        var clusters = Routes()
            .EnumerateObject()
            .Where(route => route.Value.GetProperty("Match").GetProperty("Path").GetString()!
                .StartsWith("/api/v1/admin/", StringComparison.Ordinal))
            .Select(route => route.Value.GetProperty("ClusterId").GetString()!)
            .ToList();

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var known = document.RootElement
            .GetProperty("ReverseProxy")
            .GetProperty("Clusters")
            .EnumerateObject()
            .Select(cluster => cluster.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(clusters, cluster => Assert.Contains(cluster, known));
    }
}
