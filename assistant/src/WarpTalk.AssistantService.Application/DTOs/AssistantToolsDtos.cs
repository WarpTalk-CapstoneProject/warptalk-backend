namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>
/// <c>GET api/v1/assistant/tools?workspaceId=</c>: everything WarpBot can reach for, for one member
/// of one workspace.
/// </summary>
/// <param name="ManifestAvailable">
/// Whether the AI worker's tool manifest was in Redis and readable. False when the worker is down
/// (the key has expired) or wrote something this build cannot read.
/// </param>
/// <param name="ManifestGeneratedAt">When the worker wrote the manifest; null with no manifest.</param>
/// <param name="BuiltIn">The worker's built-in tools, filtered by audience for this caller.</param>
/// <param name="WebSearch">Whether WarpBot may search the web here.</param>
/// <param name="Plugins">The plugin tools WarpBot is offered, grouped by plugin.</param>
public record AssistantToolsDto(
    bool ManifestAvailable,
    DateTime? ManifestGeneratedAt,
    IReadOnlyList<AssistantBuiltInToolDto> BuiltIn,
    AssistantWebSearchDto WebSearch,
    IReadOnlyList<AssistantPluginToolGroupDto> Plugins);

/// <summary>One built-in tool, as the worker's manifest describes it.</summary>
public record AssistantBuiltInToolDto(
    string Name,
    string Category,
    string Effect,
    string Audience,
    string Description);

/// <param name="State">One of <see cref="AssistantToolConstants.WebSearchState"/>.</param>
public record AssistantWebSearchDto(string State);

public record AssistantPluginToolGroupDto(
    string PluginKey,
    string Label,
    IReadOnlyList<AssistantPluginToolDto> Tools);

/// <param name="Policy">The member's own choice for the tool.</param>
/// <param name="WorkspacePolicy">
/// The workspace Owner's rule (<c>approval</c> or <c>blocked</c>), or null. Always serialised,
/// null included.
/// </param>
public record AssistantPluginToolDto(
    string Name,
    string Label,
    string Description,
    string Effect,
    string Policy,
    string? WorkspacePolicy);

/// <summary>
/// One plugin's tools as WarpBot is offered them, before they are flattened for the worker.
/// </summary>
public record OfferedPluginToolsDto(
    string PluginKey,
    string Label,
    IReadOnlyList<McpToolDescriptorDto> Tools);

public static class AssistantToolConstants
{
    /// <summary>The Redis key the AI worker writes its built-in tool manifest to (plain SET, JSON).</summary>
    public const string ManifestRedisKey = "assistant:tools:manifest";

    public static class Audience
    {
        public const string Member = "member";
        public const string Host = "host";
        public const string PlatformStaff = "platform_staff";
    }

    public static class WebSearchState
    {
        /// <summary>The worker can search and no setting turns it off.</summary>
        public const string On = "on";

        /// <summary>The worker can search, but a setting turns it off.</summary>
        public const string Off = "off";

        /// <summary>The worker's environment has no web search.</summary>
        public const string Unavailable = "unavailable";

        /// <summary>No manifest, so the worker's ceiling is not known.</summary>
        public const string Unknown = "unknown";
    }
}
