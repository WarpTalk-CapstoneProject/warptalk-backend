using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// Covers the shared WarpTalk.Shared system-admin policy that every ~/api/v1/admin/* endpoint
/// is gated on (WT-205). Lives here alongside the other WarpTalk.Shared tests.
/// </summary>
public sealed class SystemAdminAuthorizationTests
{
    private static IAuthorizationService BuildAuthorizationService() =>
        new ServiceCollection()
            .AddLogging()
            .AddWarpTalkSystemAdminAuthorization()
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestAuth", ClaimTypes.NameIdentifier, ClaimTypes.Role));

    private static async Task<bool> IsAllowed(ClaimsPrincipal user) =>
        (await BuildAuthorizationService()
            .AuthorizeAsync(user, resource: null, SystemAdminAuthorization.PolicyName))
        .Succeeded;

    [Fact]
    public async Task PlatformAdminRoleIsAllowed()
    {
        var user = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "admin"));

        Assert.True(await IsAllowed(user));
    }

    [Fact]
    public async Task ShortRoleClaimTypeIsAllowed()
    {
        // A token read without inbound claim mapping carries the short "role" type.
        var identity = new ClaimsIdentity(
            [new Claim("role", "admin")],
            "TestAuth",
            ClaimTypes.NameIdentifier,
            ClaimTypes.Role);

        Assert.True(await IsAllowed(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public async Task WorkspaceAdminRoleIsRejected()
    {
        // auth.roles seeds both 'admin' (platform system administrator) and 'Admin' (workspace
        // administrator). The two must never be interchangeable at an admin endpoint.
        var user = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"));

        // Pins the framework behaviour this policy relies on: IsInRole compares the claim
        // VALUE with StringComparison.Ordinal (only the claim TYPE is case-insensitive), so
        // 'Admin' is already distinct from 'admin'. If a future framework change relaxed that,
        // this assertion fails here rather than silently widening every admin endpoint.
        Assert.False(user.IsInRole("admin"));
        Assert.False(await IsAllowed(user));
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Member")]
    [InlineData("user")]
    [InlineData("moderator")]
    public async Task OtherSeededRolesAreRejected(string role)
    {
        var user = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, role));

        Assert.False(await IsAllowed(user));
    }

    [Fact]
    public async Task AuthenticatedCallerWithNoRolesIsRejected()
    {
        var user = Authenticated(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        Assert.False(await IsAllowed(user));
    }

    [Fact]
    public async Task AnonymousCallerIsRejected()
    {
        Assert.False(await IsAllowed(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task ANonRoleClaimHoldingTheValueAdminIsRejected()
    {
        // A client cannot smuggle the role in under a different claim type.
        var user = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("workspace_role", "admin"),
            new Claim("actor", "admin"));

        Assert.False(await IsAllowed(user));
    }
}
