using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Mappers;

internal static class PluginCatalogItemMapper
{
    public static PluginCatalogItemDto ToCatalogItem(
        PluginDefinitionDto plugin,
        PluginInstallation? installation,
        PluginConnection? connection,
        string? workspacePolicyBlockReason = null)
    {
        var installationStatus = installation?.Status ?? PluginConstants.InstallationStatus.NotInstalled;
        // The grant is the provider's; being connected is the plugin's. A row the user never
        // connected reads not_connected even while a sibling's grant already covers its scopes -
        // otherwise connecting Calendar switched Meet (same scope) and Drive (a scope Google
        // remembered) on with it. Email and granted scopes still come from the grant, because they
        // describe the account the plugin would connect to.
        var connectionStatus = installation?.ConnectedAt is null
            ? PluginConstants.ConnectionStatus.NotConnected
            : connection?.Status ?? PluginConstants.ConnectionStatus.NotConnected;
        var grantedScopes = connection == null
            ? Array.Empty<string>()
            : PluginScopeMapper.FromJson(connection.ScopesJson);

        return new PluginCatalogItemDto(
            plugin.Key,
            plugin.Provider,
            plugin.Label,
            plugin.Description,
            plugin.AvatarUrl,
            plugin.RequiredScopes,
            installationStatus,
            connectionStatus,
            connection?.ProviderEmail,
            // Each tool carries this user's choice for it, so the Manage dialog shows what WarpBot
            // will actually do rather than what the tool's effect alone would suggest. WT-687.
            PluginToolPolicyStore.WithPolicies(plugin.Tools, installation?.ConfigJson),
            grantedScopes,
            workspacePolicyBlockReason,
            plugin.IsFeatured,
            plugin.SortOrder,
            plugin.Category,
            plugin.AuthMode);
    }
}
