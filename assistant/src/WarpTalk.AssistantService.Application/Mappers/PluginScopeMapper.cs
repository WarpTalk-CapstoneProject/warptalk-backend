using System.Text.Json;

namespace WarpTalk.AssistantService.Application.Mappers;

internal static class PluginScopeMapper
{
    public static IReadOnlyList<string> FromJson(string scopesJson)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(scopesJson) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
