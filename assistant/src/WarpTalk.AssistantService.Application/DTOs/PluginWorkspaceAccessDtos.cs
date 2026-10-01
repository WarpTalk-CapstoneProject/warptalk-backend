namespace WarpTalk.AssistantService.Application.DTOs;

// ---------------------------------------------------------------------------------------------
// Platform-admin control over which workspaces a marketplace plugin reaches (2026-09-25).
// 20260925120000_plugin_workspace_availability.sql has the layers; PluginWorkspaceAccess applies them.
// ---------------------------------------------------------------------------------------------

/// <summary>A plugin's platform default, as the admin picks it.</summary>
/// <param name="Default"><c>available</c>, <c>opt_in</c> or <c>retired</c>.</param>
/// <param name="AllowedPlans">Null: every plan. Otherwise only workspaces on one of these plans.</param>
public record PluginAvailabilityDto(string Default, IReadOnlyList<string>? AllowedPlans);

/// <summary>Replaces both halves of a plugin's default: PUT semantics.</summary>
/// <param name="AllowedPlans">Null or empty: every plan.</param>
public record SetPluginAvailabilityRequest(string? Default, IReadOnlyList<string>? AllowedPlans = null);

/// <summary>One marketplace plugin in one workspace, as both admin tabs list it.</summary>
/// <param name="Enabled">The platform's verdict: may this workspace have the plugin at all.</param>
/// <param name="Source">
/// Which layer decided: <c>retired</c>, <c>override</c>, <c>plan</c> or <c>default</c> - "inherited"
/// is everything but <c>override</c>.
/// </param>
/// <param name="OnWorkspaceList">The workspace Owner's list holds it (or carries it over).</param>
/// <param name="InUse">Enabled, and on the list or connected by a member: what "using it" counts.</param>
/// <param name="ConnectedUserIds">Active members of the workspace who connected it.</param>
/// <param name="UsageCount">Successful WarpBot tool calls of the plugin in this workspace.</param>
public record PluginWorkspaceRowDto(
    Guid WorkspaceId,
    string WorkspaceName,
    string WorkspaceSlug,
    string WorkspaceStatus,
    string? PlanSlug,
    int MemberCount,
    string PluginKey,
    string PluginLabel,
    string? PluginAvatarUrl,
    string PluginKind,
    string PluginDefault,
    IReadOnlyList<string>? AllowedPlans,
    bool Enabled,
    string Source,
    string? OverrideState,
    string? OverrideReason,
    Guid? OverrideSetBy,
    DateTime? OverrideSetAt,
    bool OnWorkspaceList,
    bool InUse,
    IReadOnlyList<Guid> ConnectedUserIds,
    int UsageCount,
    DateTime? LastUsedAt);

/// <summary>The plugin detail page's "Workspaces" tab: every workspace, one plugin.</summary>
public record PluginWorkspacesDto(
    string PluginKey,
    string Label,
    PluginAvailabilityDto Availability,
    IReadOnlyList<PluginWorkspaceRowDto> Workspaces);

/// <summary>The admin workspace page's "Plugins" tab: every marketplace plugin, one workspace.</summary>
public record WorkspacePluginsAdminDto(
    Guid WorkspaceId,
    string WorkspaceName,
    string? PlanSlug,
    IReadOnlyList<PluginWorkspaceRowDto> Plugins);

/// <summary>Enable, disable or reset one plugin on many workspaces at once.</summary>
/// <param name="Action"><c>enable</c>, <c>disable</c> or <c>reset</c>.</param>
/// <param name="Reason">Required to disable; recorded on the override and in the audit log.</param>
/// <param name="WorkspaceIds">
/// The workspaces to change. With <paramref name="PlanSlugs"/> too, only those of them on one of
/// the plans.
/// </param>
/// <param name="PlanSlugs">Alone: every workspace on one of these plans.</param>
public record ApplyPluginOverrideRequest(
    string? Action,
    string? Reason = null,
    IReadOnlyList<Guid>? WorkspaceIds = null,
    IReadOnlyList<string>? PlanSlugs = null);

/// <summary>What a bulk apply did.</summary>
/// <param name="Changed">Workspaces whose override was written or removed.</param>
/// <param name="Unchanged">Targets already in the requested state.</param>
public record ApplyPluginOverrideResultDto(
    string PluginKey,
    string Action,
    int Changed,
    int Unchanged,
    IReadOnlyList<Guid> WorkspaceIds);

/// <summary>Enable, disable or reset one plugin in one workspace.</summary>
public record SetWorkspacePluginOverrideRequest(string? Action, string? Reason = null);
