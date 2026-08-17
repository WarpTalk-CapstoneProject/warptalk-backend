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
        "/api/v1/admin/audit-log/{**catch-all}",
        "/api/v1/admin/billing/{**catch-all}",
        // The platform user directory, served by auth. Read-only: the controller has no mutation,
        // because auditing one would need a message bus the auth service deliberately does not
        // have.
        "/api/v1/admin/users/{**catch-all}",
        // The subscription directory and its revenue summary, served by billing. Read-only:
        // changing a plan or cancelling a subscription already has its own validated path.
        "/api/v1/admin/subscriptions/{**catch-all}",
        // The platform meeting directory, served by translation-room. Metadata only: no join, no
        // room control, and no transcript read anywhere on that controller.
        "/api/v1/admin/meetings/{**catch-all}",
        // The System Health screen, served by workspace. Query-only against the metrics store:
        // nothing behind it can silence an alert, restart a container or write a sample.
        "/api/v1/admin/platform-health/{**catch-all}",
        // Product feedback, served by translation-room. Read-only and aggregated; comments come
        // back without the person who wrote them.
        "/api/v1/admin/feedback/{**catch-all}",
        // The language catalog room validation reads, inactive rows included. Read-only:
        // deactivating a language stops every new room in it platform-wide, and translation-room
        // has no message bus to record who did it.
        "/api/v1/admin/languages/{**catch-all}",
        // Voice-clone consent, served by auth. COUNTS ONLY — a per-person list of who agreed to
        // being cloned is a register of biometric permissions, and nothing here acts on a person.
        "/api/v1/admin/voice-consent/{**catch-all}",
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
