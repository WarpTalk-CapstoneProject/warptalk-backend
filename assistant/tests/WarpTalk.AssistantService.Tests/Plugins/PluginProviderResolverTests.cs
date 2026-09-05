using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Infrastructure.Plugins;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// Dispatch is what makes "add an app" a catalog row instead of a deploy, so it is pinned here
/// rather than left to integration testing.
/// </summary>
public class PluginProviderResolverTests
{
    private readonly IMcpToolGateway _nativeGateway = Substitute.For<IMcpToolGateway>();
    private readonly IMcpToolGateway _mcpGateway = Substitute.For<IMcpToolGateway>();
    private readonly IPluginOAuthClient _nativeOAuth = Substitute.For<IPluginOAuthClient>();
    private readonly IPluginOAuthClient _mcpOAuth = Substitute.For<IPluginOAuthClient>();

    [Fact]
    public void ResolveGateway_PicksTheImplementationRegisteredForThatKind()
    {
        var resolver = CreateResolver();

        Assert.Same(_nativeGateway, resolver.ResolveGateway(PluginConstants.PluginKind.Native));
        Assert.Same(_mcpGateway, resolver.ResolveGateway(PluginConstants.PluginKind.Mcp));
    }

    [Fact]
    public void ResolveOAuthClient_PicksTheImplementationRegisteredForThatKind()
    {
        var resolver = CreateResolver();

        Assert.Same(_nativeOAuth, resolver.ResolveOAuthClient(PluginConstants.PluginKind.Native));
        Assert.Same(_mcpOAuth, resolver.ResolveOAuthClient(PluginConstants.PluginKind.Mcp));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ResolveGateway_TreatsAMissingKindAsNative(string? kind)
    {
        // Rows written before `kind` existed read back blank. Every one of them predates the MCP
        // path, so blank has to mean native or an upgrade would break existing installs.
        Assert.Same(_nativeGateway, CreateResolver().ResolveGateway(kind!));
    }

    [Fact]
    public void ResolveGateway_ThrowsWhenTheKindHasNoImplementation()
    {
        // The database constrains `kind`, so reaching this means the catalog and the code have
        // drifted apart - a bug to surface loudly, not an expected failure to degrade around.
        var exception = Assert.Throws<NotSupportedException>(
            () => CreateResolver().ResolveGateway("carrier_pigeon"));

        Assert.Contains("carrier_pigeon", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOAuthClient_ThrowsWhenTheKindHasNoImplementation()
    {
        Assert.Throws<NotSupportedException>(() => CreateResolver().ResolveOAuthClient("carrier_pigeon"));
    }

    private PluginProviderResolver CreateResolver()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(PluginConstants.PluginKind.Native, _nativeGateway);
        services.AddKeyedSingleton(PluginConstants.PluginKind.Mcp, _mcpGateway);
        services.AddKeyedSingleton(PluginConstants.PluginKind.Native, _nativeOAuth);
        services.AddKeyedSingleton(PluginConstants.PluginKind.Mcp, _mcpOAuth);

        return new PluginProviderResolver(services.BuildServiceProvider());
    }
}
