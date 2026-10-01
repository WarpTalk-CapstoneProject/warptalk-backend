using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// A workspace Owner's per-tool rules (<c>workspace_plugin_tool_policies</c>), read and applied.
/// </summary>
/// <remarks>
/// A rule only ever tightens: WarpBot gets <see cref="PluginConstants.ToolPolicy.Strictest"/> of
/// the member's own choice (WT-687) and the rule. Rules are keyed by plugin id and then by tool name,
/// compared ordinally like every other tool-name lookup on the execution path.
/// </remarks>
public static class WorkspaceToolRules
{
    public static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Every rule the workspace has for these plugins, read in one query.</summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> LoadAsync(
        IUnitOfWork unitOfWork,
        Guid workspaceId,
        IReadOnlyCollection<Guid> pluginIds,
        CancellationToken ct)
    {
        if (pluginIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyDictionary<string, string>>();

        var ids = pluginIds.ToHashSet();
        var rows = await unitOfWork.WorkspacePluginToolPolicyRepository.FindAsync(
            r => r.WorkspaceId == workspaceId && ids.Contains(r.PluginId), ct: ct);

        return rows
            // A value this build does not know is ignored rather than trusted, as the member's
            // store does: it falls back to the member's choice instead of silently meaning anything.
            .Where(r => PluginConstants.ToolPolicy.IsWorkspaceRule(r.Policy))
            .GroupBy(r => r.PluginId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group
                    .GroupBy(r => r.ToolName, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Policy, StringComparer.Ordinal));
    }

    public static IReadOnlyDictionary<string, string> ForPlugin(
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> rules,
        Guid pluginId) =>
        rules.TryGetValue(pluginId, out var forPlugin) ? forPlugin : None;

    public static string? For(IReadOnlyDictionary<string, string> rules, string toolName) =>
        rules.TryGetValue(toolName, out var rule) ? rule : null;

    /// <summary>
    /// Tools that already carry the member's <see cref="McpToolDescriptorDto.Policy"/>, with the
    /// workspace's rule added beside it. When <paramref name="effective"/> is set, Policy becomes
    /// the stricter of the two - what WarpBot will actually do; otherwise it stays the member's own
    /// choice, which is what the member's settings dialog edits.
    /// </summary>
    public static IReadOnlyList<McpToolDescriptorDto> Apply(
        IReadOnlyList<McpToolDescriptorDto> tools,
        IReadOnlyDictionary<string, string> rules,
        bool effective)
    {
        if (rules.Count == 0) return tools;

        return tools.Select(tool =>
        {
            var rule = For(rules, tool.Name);
            if (rule is null) return tool;
            var memberPolicy = tool.Policy ?? PluginConstants.ToolPolicy.DefaultFor(tool.Effect);
            return tool with
            {
                WorkspacePolicy = rule,
                Policy = effective ? PluginConstants.ToolPolicy.Strictest(memberPolicy, rule) : tool.Policy,
            };
        }).ToList();
    }
}
