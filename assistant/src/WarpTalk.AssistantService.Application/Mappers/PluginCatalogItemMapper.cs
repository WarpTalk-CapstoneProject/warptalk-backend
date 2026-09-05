using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Mappers;

internal static class PluginCatalogItemMapper
{
    public static PluginCatalogItemDto ToCatalogItem(
        PluginDefinitionDto plugin,
        PluginInstallation? installation,
        PluginConnection? connection)
    {
        var installationStatus = installation?.Status ?? PluginConstants.InstallationStatus.NotInstalled;
        var connectionStatus = connection?.Status ?? PluginConstants.ConnectionStatus.NotConnected;
        var grantedScopes = connection == null
            ? Array.Empty<string>()
            : PluginScopeMapper.FromJson(connection.ScopesJson);

        return new PluginCatalogItemDto(
            plugin.Key,
            plugin.Label,
            plugin.Description,
            plugin.AvatarUrl,
            plugin.RequiredScopes,
            installationStatus,
            connectionStatus,
            connection?.ProviderEmail,
            plugin.Tools,
            grantedScopes);
    }
}
