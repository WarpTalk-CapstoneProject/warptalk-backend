using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Mappers;

internal static class PluginDefinitionMapper
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static PluginDefinitionDto ToDefinition(Plugin plugin)
    {
        var requiredScopes = JsonSerializer.Deserialize<IReadOnlyList<string>>(plugin.RequiredScopesJson, JsonOptions)
            ?? Array.Empty<string>();
        var tools = JsonSerializer.Deserialize<IReadOnlyList<McpToolDescriptorDto>>(plugin.ToolsJson, JsonOptions)
            ?? Array.Empty<McpToolDescriptorDto>();

        return new PluginDefinitionDto(
            plugin.Id,
            plugin.PluginKey,
            plugin.Label,
            plugin.Description,
            plugin.AvatarUrl,
            requiredScopes,
            tools);
    }
}
