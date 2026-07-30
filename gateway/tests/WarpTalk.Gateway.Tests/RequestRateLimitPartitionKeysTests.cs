using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WarpTalk.Gateway.Services;

namespace WarpTalk.Gateway.Tests;

public sealed class RequestRateLimitPartitionKeysTests
{
    [Fact]
    public void AuthenticatedRequestPartitionsByIpUserAndWorkspace()
    {
        var workspaceId = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "user-123")],
                authenticationType: "test"))
        };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.10");
        context.Request.Headers["X-Workspace-Id"] = workspaceId.ToString();

        Assert.Equal("203.0.113.10", RequestRateLimitPartitionKeys.Ip(context));
        Assert.Equal("user-123", RequestRateLimitPartitionKeys.User(context));
        Assert.Equal(workspaceId.ToString(), RequestRateLimitPartitionKeys.Workspace(context));
    }

    [Fact]
    public void InvalidWorkspaceHeaderDoesNotCreateAnAttackerControlledPartition()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Workspace-Id"] = "arbitrary-partition";

        Assert.Null(RequestRateLimitPartitionKeys.Workspace(context));
    }

    [Fact]
    public void AuthenticatedWorkspaceClaimTakesPrecedenceOverClientHeader()
    {
        var trustedWorkspaceId = Guid.NewGuid();
        var spoofedWorkspaceId = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("workspace_id", trustedWorkspaceId.ToString())],
                authenticationType: "test"))
        };
        context.Request.Headers["X-Workspace-Id"] = spoofedWorkspaceId.ToString();

        Assert.Equal(
            trustedWorkspaceId.ToString(),
            RequestRateLimitPartitionKeys.Workspace(context));
    }

    [Fact]
    public void GatewayProcessesForwardedHeadersBeforeRateLimiting()
    {
        var source = File.ReadAllText(FindGatewayProgram());
        var forwardedHeadersIndex = source.IndexOf(
            "app.UseForwardedHeaders()",
            StringComparison.Ordinal);
        var rateLimiterIndex = source.IndexOf(
            "app.UseRateLimiter()",
            StringComparison.Ordinal);

        Assert.True(forwardedHeadersIndex >= 0);
        Assert.True(rateLimiterIndex > forwardedHeadersIndex);
        Assert.Contains("KnownIPNetworks", source, StringComparison.Ordinal);
    }

    private static string FindGatewayProgram()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "gateway/src/WarpTalk.Gateway/Program.cs");
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException("Could not locate Gateway Program.cs.");
    }
}
