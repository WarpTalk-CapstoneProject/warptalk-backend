using System.Text.Json;

namespace WarpTalk.Gateway.Tests;

public sealed class BillingPaymentRoutesContractTests
{
    [Fact]
    public void StripeWebhookRoute_Should_BypassAuthenticatedPaymentsCatchAll()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindGatewayAppSettings()));
        var routes = doc.RootElement
            .GetProperty("ReverseProxy")
            .GetProperty("Routes");

        var webhookRoute = routes.GetProperty("stripe-webhook-route");
        Assert.Equal("/api/v1/payments/webhook/stripe", webhookRoute.GetProperty("Match").GetProperty("Path").GetString());
        Assert.Equal(-10, webhookRoute.GetProperty("Order").GetInt32());
        Assert.False(webhookRoute.TryGetProperty("AuthorizationPolicy", out _));

        var transform = webhookRoute.GetProperty("Transforms")[0].GetProperty("PathPattern").GetString();
        Assert.Equal("/api/v1/payments/webhook/stripe", transform);

        var paymentsRoute = routes.GetProperty("billing-payments-route");
        Assert.Equal("RequireAuth", paymentsRoute.GetProperty("AuthorizationPolicy").GetString());
    }

    [Fact]
    public void PlansRootRoute_Should_ProxyPublicPlanList()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindGatewayAppSettings()));
        var routes = doc.RootElement
            .GetProperty("ReverseProxy")
            .GetProperty("Routes");

        var plansRootRoute = routes.GetProperty("billing-plans-root-route");
        Assert.Equal("/api/v1/plans", plansRootRoute.GetProperty("Match").GetProperty("Path").GetString());
        Assert.Equal(0, plansRootRoute.GetProperty("Order").GetInt32());
        Assert.False(plansRootRoute.TryGetProperty("AuthorizationPolicy", out _));
    }

    private static string FindGatewayAppSettings()
    {
        var current = Directory.GetCurrentDirectory();
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(
                current,
                "gateway/src/WarpTalk.Gateway/appsettings.json"));
            if (File.Exists(candidate)) return candidate;

            var parent = Directory.GetParent(current);
            if (parent is null) break;
            current = parent.FullName;
        }

        throw new FileNotFoundException("Could not locate Gateway appsettings.json.");
    }
}
