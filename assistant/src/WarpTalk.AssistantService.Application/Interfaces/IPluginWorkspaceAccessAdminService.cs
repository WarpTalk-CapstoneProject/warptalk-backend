using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The platform admin's per-workspace plugin controls: a plugin's default, overrides for one or
/// many workspaces, and the two views of the result (one plugin across workspaces, one workspace
/// across plugins).
/// </summary>
/// <remarks>
/// Gated on the platform-admin policy at the controller. Every write is recorded in the platform
/// audit log BEFORE it is committed and abandoned when the record fails, like the rest of the
/// catalog's admin writes.
/// <para>
/// Enforcement is not here: it is the guard's, through <c>WorkspacePluginAvailability</c>, on every
/// catalog listing, install, connect and tool call. This service only writes what the guard reads.
/// </para>
/// </remarks>
public interface IPluginWorkspaceAccessAdminService
{
    Task<Result<PluginWorkspacesDto>> GetForPluginAsync(string pluginKey, CancellationToken ct = default);

    Task<Result<PluginAvailabilityDto>> SetAvailabilityAsync(
        string pluginKey,
        SetPluginAvailabilityRequest request,
        Guid adminUserId,
        CancellationToken ct = default);

    Task<Result<ApplyPluginOverrideResultDto>> ApplyOverrideAsync(
        string pluginKey,
        ApplyPluginOverrideRequest request,
        Guid adminUserId,
        CancellationToken ct = default);

    Task<Result<WorkspacePluginsAdminDto>> GetForWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);

    Task<Result<PluginWorkspaceRowDto>> SetWorkspaceOverrideAsync(
        Guid workspaceId,
        string pluginKey,
        SetWorkspacePluginOverrideRequest request,
        Guid adminUserId,
        CancellationToken ct = default);
}
