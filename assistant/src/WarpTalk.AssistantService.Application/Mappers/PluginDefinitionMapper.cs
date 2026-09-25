using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Mappers;

internal static class PluginDefinitionMapper
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static PluginDefinitionDto ToDefinition(Plugin plugin)
    {
        var requiredScopes = JsonSerializer.Deserialize<IReadOnlyList<string>>(plugin.RequiredScopesJson, JsonOptions)
            ?? Array.Empty<string>();
        var stored = JsonSerializer.Deserialize<IReadOnlyList<McpToolDescriptorDto>>(plugin.ToolsJson, JsonOptions)
            ?? Array.Empty<McpToolDescriptorDto>();

        // Two things are decided by the row, never by what tools_json happens to say, because
        // every reader - the tool list, execution, the catalog - comes through here:
        //
        //   - PluginKey. A tool belongs to the row whose manifest holds it, and a call is resolved
        //     back to a plugin by this key. Every writer already stamps it, but a stored value that
        //     named another row would send that tool's calls to the other plugin; stamping it on
        //     the way out makes that impossible rather than merely unlikely.
        //   - Effect, for a private row. Its server is whatever a workspace Owner typed in, so its
        //     readOnlyHint is its own unverified word that a tool changes nothing - and "read" is
        //     what lets a tool run without a confirmation card. McpToolGateway already ignores the
        //     hint for such a row; this also covers manifests synced before it did.
        var isPrivate = plugin.OwnerWorkspaceId is not null;
        var tools = stored
            .Select(tool => tool with
            {
                PluginKey = plugin.PluginKey,
                Effect = isPrivate ? PluginConstants.ToolEffect.Write : tool.Effect,
            })
            .ToList();

        return new PluginDefinitionDto(
            plugin.Id,
            plugin.PluginKey,
            plugin.Provider,
            plugin.Label,
            plugin.Description,
            plugin.AvatarUrl,
            requiredScopes,
            tools,
            plugin.Kind,
            plugin.McpServerUrl,
            plugin.IsFeatured,
            plugin.SortOrder,
            plugin.Category,
            PluginConstants.AuthMode.Of(plugin.OAuthClientSource),
            plugin.OwnerWorkspaceId);
    }
}
