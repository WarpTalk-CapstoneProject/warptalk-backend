using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>An API-key row never starts an OAuth flow, and never becomes one behind the admin's back.</summary>
public class McpClientProvisionerApiKeyTests
{
    [Fact]
    public async Task ProvisionAsync_RefusesAnApiKeyRow_WithoutRunningDiscovery()
    {
        var discovery = Substitute.For<IMcpAuthorizationServerDiscovery>();
        var resolver = Substitute.For<IMcpClientRegistrationResolver>();
        var sut = new McpClientProvisioner(Substitute.For<IUnitOfWork>(), discovery, resolver);
        var plugin = new Plugin
        {
            PluginKey = "linear",
            Label = "Linear",
            Kind = PluginConstants.PluginKind.Mcp,
            McpServerUrl = "https://mcp.linear.app/mcp",
            OAuthClientSource = PluginConstants.OAuthClientSource.ApiKey,
        };

        var result = await sut.ProvisionAsync(plugin);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ApiKeyRequired, result.ErrorCode);
        Assert.Equal(PluginConstants.OAuthClientSource.ApiKey, plugin.OAuthClientSource);
        await discovery.DidNotReceive().DiscoverAsync(Arg.Any<Plugin>(), Arg.Any<CancellationToken>());
    }
}
