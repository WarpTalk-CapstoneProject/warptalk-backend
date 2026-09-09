using Microsoft.Extensions.DependencyInjection;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;

namespace WarpTalk.AssistantService.Infrastructure.Plugins;

/// <inheritdoc />
public class PluginProviderResolver : IPluginProviderResolver
{
    private readonly IServiceProvider _serviceProvider;

    public PluginProviderResolver(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IMcpToolGateway ResolveGateway(string pluginKind) =>
        _serviceProvider.GetKeyedService<IMcpToolGateway>(Normalize(pluginKind))
        ?? throw new NotSupportedException($"No MCP tool gateway is registered for plugin kind '{pluginKind}'.");

    public IPluginOAuthClient ResolveOAuthClient(string pluginKind) =>
        _serviceProvider.GetKeyedService<IPluginOAuthClient>(Normalize(pluginKind))
        ?? throw new NotSupportedException($"No OAuth client is registered for plugin kind '{pluginKind}'.");

    /// <summary>
    /// Rows written before <c>kind</c> existed, or hand-inserted rows, can carry null/blank. Those
    /// are all pre-MCP providers, so treating blank as <c>native</c> keeps them working.
    /// </summary>
    private static string Normalize(string pluginKind) =>
        string.IsNullOrWhiteSpace(pluginKind) ? PluginConstants.PluginKind.Native : pluginKind;
}
