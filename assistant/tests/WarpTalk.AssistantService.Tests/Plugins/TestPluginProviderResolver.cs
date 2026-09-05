using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// Resolves every plugin kind to the one gateway/OAuth client a test set up.
/// </summary>
/// <remarks>
/// Production dispatches on <c>Plugin.Kind</c> so an MCP-backed app needs no code. These tests are
/// about the orchestration and connection logic that sits <em>above</em> that choice, so they pin a
/// single pair rather than exercising the dispatch itself - dispatch has its own tests.
/// </remarks>
internal sealed class TestPluginProviderResolver : IPluginProviderResolver
{
    private readonly IMcpToolGateway? _gateway;
    private readonly IPluginOAuthClient? _oauthClient;

    public TestPluginProviderResolver(IMcpToolGateway? gateway = null, IPluginOAuthClient? oauthClient = null)
    {
        _gateway = gateway;
        _oauthClient = oauthClient;
    }

    public IMcpToolGateway ResolveGateway(string pluginKind) =>
        _gateway ?? throw new InvalidOperationException("This test did not register a gateway.");

    public IPluginOAuthClient ResolveOAuthClient(string pluginKind) =>
        _oauthClient ?? throw new InvalidOperationException("This test did not register an OAuth client.");
}
