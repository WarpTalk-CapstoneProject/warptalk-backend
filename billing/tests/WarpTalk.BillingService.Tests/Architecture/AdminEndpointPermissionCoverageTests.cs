using WarpTalk.Shared.Authorization;
using Xunit;

namespace WarpTalk.BillingService.Tests.Architecture;

/// <summary>
/// G10: every admin endpoint in this service requires exactly one staff permission.
///
/// Fails when an endpoint under api/v1/admin (or one carrying [RequirePermission]) has no
/// permission, two, a code that is not in AdminPermissions, anonymous access, or is still behind
/// the pre-G10 system-admin role/policy. The rule itself lives in
/// WarpTalk.Shared.Authorization.AdminEndpointPermissionCoverage so every service runs the same one.
/// </summary>
public sealed class AdminEndpointPermissionCoverageTests
{
    private static readonly System.Reflection.Assembly Api = typeof(WarpTalk.BillingService.API.Controllers.AdminBillingInsightsController).Assembly;

    [Fact]
    public void EveryAdminEndpoint_RequiresExactlyOnePermission()
    {
        var violations = AdminEndpointPermissionCoverage.Violations(Api);

        Assert.True(violations.Count == 0, string.Join(System.Environment.NewLine, violations));
    }

    [Fact]
    public void TheScanSeesThisServicesAdminEndpoints()
    {
        // Guards the test above against passing vacuously (a scan that found nothing).
        Assert.NotEmpty(AdminEndpointPermissionCoverage.AdminEndpoints(Api));
    }
}
