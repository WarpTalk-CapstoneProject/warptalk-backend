namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>One plugin as the workspace Owner's Plugins page shows it.</summary>
/// <param name="Availability">
/// <c>added</c>, <c>private</c> or <c>not_added</c> - see <c>WorkspacePluginConstants.Availability</c>.
/// </param>
/// <param name="McpServerUrl">
/// Only for a private plugin, which the Owner created and may edit. A marketplace row's server is
/// the platform's business, not the workspace's.
/// </param>
/// <param name="AddedBy">Null for a row the transition seeded, and for every marketplace candidate.</param>
/// <param name="MembersUsedCount">
/// Distinct members who have run one of its tools in this workspace. Connections are personal and
/// not workspace-scoped, so "members connected" is not something this service can count honestly;
/// the tool audit trail is.
/// </param>
public record WorkspacePluginItemDto(
    string Key,
    string Provider,
    string Label,
    string Description,
    string? AvatarUrl,
    string Kind,
    string Availability,
    string? McpServerUrl,
    Guid? AddedBy,
    DateTime? AddedAt,
    int MembersUsedCount);

public record WorkspacePluginRequestDto(
    Guid Id,
    Guid WorkspaceId,
    string PluginKey,
    string PluginLabel,
    string? PluginAvatarUrl,
    Guid RequestedBy,
    string? Reason,
    string Status,
    DateTime CreatedAt,
    Guid? DecidedBy,
    DateTime? DecidedAt);

/// <summary>Everything the workspace Plugins page renders, in one read.</summary>
/// <param name="IsCurated">
/// False while the workspace is still on the pre-marketplace AllowAnyPlugins default. Every
/// marketplace plugin then reads as added (or none does, if the switch was off), and the first
/// change the Owner makes turns that into an explicit list.
/// </param>
/// <param name="CanManage">Whether the caller may change the list - the Owner, not an Admin.</param>
public record WorkspacePluginsOverviewDto(
    Guid WorkspaceId,
    bool IsCurated,
    bool CanManage,
    IReadOnlyList<WorkspacePluginItemDto> InWorkspace,
    IReadOnlyList<WorkspacePluginItemDto> Marketplace,
    IReadOnlyList<WorkspacePluginRequestDto> PendingRequests);

/// <param name="Reason">Optional, at most 500 characters.</param>
public record CreatePluginRequestRequest(string PluginKey, string? Reason = null);

/// <summary>A private MCP plugin, visible only in the workspace that creates it.</summary>
/// <remarks>
/// No key: the service derives one. A key is also the plugin's OAuth provider identity, so letting
/// an Owner choose it would let them collide with - and be handed - someone else's grant.
/// </remarks>
public record CreatePrivatePluginRequest(string Label, string McpServerUrl, string? Description = null);

/// <summary>Only the fields present are changed.</summary>
public record UpdatePrivatePluginRequest(string? Label = null, string? Description = null, string? McpServerUrl = null);
