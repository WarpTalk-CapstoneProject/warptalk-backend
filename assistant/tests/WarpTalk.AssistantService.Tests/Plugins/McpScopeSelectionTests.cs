using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>Which scopes an MCP authorization request asks for. WT-710.</summary>
public class McpScopeSelectionTests
{
    [Fact]
    public void PrefersTheChallengedScopes_OverEitherScopesSupportedList()
    {
        var scopes = McpScopeSelection.Select([], Discovery(challenged: ["files:read"], resource: ["files:read", "files:write"], server: ["openid"]));

        Assert.Equal(["files:read"], scopes);
    }

    [Fact]
    public void FallsBackToTheResourceList_ThenToTheAuthorizationServerList()
    {
        Assert.Equal(["mcp:read"], McpScopeSelection.Select([], Discovery(resource: ["mcp:read"], server: ["openid"])));
        Assert.Equal(["openid"], McpScopeSelection.Select([], Discovery(server: ["openid"])));
    }

    [Fact]
    public void KeepsTheRowsDeclaredScopes_AndAddsWhatTheServerChallengedFor()
    {
        var scopes = McpScopeSelection.Select(["mcp:read"], Discovery(challenged: ["mcp:read", "mcp:write"], resource: ["other"]));

        Assert.Equal(["mcp:read", "mcp:write"], scopes);
    }

    [Fact]
    public void AsksForOfflineAccess_OnlyWhenTheAuthorizationServerOffersIt()
    {
        Assert.Contains(McpScopeSelection.OfflineAccess, McpScopeSelection.Select([], Discovery(resource: ["mcp"], server: ["mcp", "offline_access"])));
        Assert.DoesNotContain(McpScopeSelection.OfflineAccess, McpScopeSelection.Select([], Discovery(resource: ["mcp"], server: ["mcp"])));
    }

    [Fact]
    public void LeavesANativeRowAlone_WhenThereWasNoDiscovery()
    {
        Assert.Equal(["https://www.googleapis.com/auth/drive.readonly"], McpScopeSelection.Select(["https://www.googleapis.com/auth/drive.readonly"], null));
    }

    private static McpServerDiscoveryDto Discovery(
        string[]? challenged = null,
        string[]? resource = null,
        string[]? server = null) =>
        new(
            "https://remote.test/mcp",
            resource ?? [],
            new AuthorizationServerMetadataDto(
                "https://auth.remote.test",
                "https://auth.remote.test/authorize",
                "https://auth.remote.test/token",
                null,
                null,
                ClientIdMetadataDocumentSupported: false,
                IssParameterSupported: false,
                ["S256"],
                ["none"],
                server ?? []),
            challenged);
}
