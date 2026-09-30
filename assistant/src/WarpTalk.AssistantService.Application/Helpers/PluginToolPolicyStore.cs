using System.Text.Json.Nodes;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Constants;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// Reads and writes the per-tool choices a user has made, kept in their installation's
/// <c>config_json</c> under <c>toolPolicy</c>. WT-687.
/// </summary>
/// <remarks>
/// The installation is the right home: it is already one row per user per plugin, and removing a
/// plugin is what should forget the choices made about it. Keeping it inside the existing JSON
/// column means no migration, and the other keys already stored there (<c>installedFrom</c>) are
/// carried through untouched.
/// <para>
/// Only choices are stored, never defaults. A tool the user has not touched resolves from its
/// effect each time, so a tool that <c>tools/list</c> adds later arrives with the safe default
/// rather than with whatever a snapshot taken earlier would have said.
/// </para>
/// </remarks>
public static class PluginToolPolicyStore
{
    private const string PolicyKey = "toolPolicy";

    public static IReadOnlyDictionary<string, string> Read(string? configJson)
    {
        var policies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ParseObject(configJson)?[PolicyKey] is not JsonObject stored) return policies;

        foreach (var (toolName, value) in stored)
        {
            // A value this build does not recognise is ignored rather than trusted, so it falls back
            // to the default instead of silently meaning "allow".
            if (value is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out var policy)
                && PluginConstants.ToolPolicy.IsKnown(policy))
            {
                policies[toolName] = policy;
            }
        }

        return policies;
    }

    public static string Resolve(McpToolDescriptorDto tool, IReadOnlyDictionary<string, string> policies) =>
        policies.TryGetValue(tool.Name, out var policy)
            ? policy
            : PluginConstants.ToolPolicy.DefaultFor(tool.Effect);

    /// <summary>The tools with <see cref="McpToolDescriptorDto.Policy"/> filled in for one user.</summary>
    public static IReadOnlyList<McpToolDescriptorDto> WithPolicies(
        IReadOnlyList<McpToolDescriptorDto> tools,
        string? configJson)
    {
        var policies = Read(configJson);
        return tools.Select(tool => tool with { Policy = Resolve(tool, policies) }).ToList();
    }

    /// <summary>
    /// Merges <paramref name="updates"/> into the stored choices and returns the new column value.
    /// Tools not named in <paramref name="updates"/> keep what they had.
    /// </summary>
    public static string Write(string? configJson, IReadOnlyDictionary<string, string> updates)
    {
        var config = ParseObject(configJson) ?? new JsonObject();
        if (config[PolicyKey] is not JsonObject stored)
        {
            stored = new JsonObject();
            config[PolicyKey] = stored;
        }

        foreach (var (toolName, policy) in updates)
            stored[toolName] = policy;

        return config.ToJsonString();
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
