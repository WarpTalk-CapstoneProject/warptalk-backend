using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

/// <summary>
/// Which workspaces a marketplace plugin reaches: its platform default, per-workspace overrides
/// (one or many at once), and both views of the result.
/// </summary>
/// <remarks>
/// Two prefixes, one per admin page. The plugin-centric routes sit under the catalog's own
/// <c>plugins/catalog/{pluginKey}</c>, beside the row they change; the workspace-centric ones under
/// <c>admin/workspaces/{workspaceId}</c>, a prefix of their own so no literal is added beside a
/// <c>{pluginKey}</c> route (which would reserve a plugin key). The gateway's
/// <c>/api/v1/assistant/{**catch-all}</c> covers both.
/// <para>
/// Gated on the platform-admin policy. Enforcement is not here - it is the guard's, on every
/// catalog listing, install, connect and tool call.
/// </para>
/// </remarks>
[ApiController]
public class AdminPluginWorkspaceAccessController : ControllerBase
{
    private readonly IPluginWorkspaceAccessAdminService _service;

    public AdminPluginWorkspaceAccessController(IPluginWorkspaceAccessAdminService service)
    {
        _service = service;
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    /// <summary>Every workspace, with this plugin's effective state there.</summary>
    [HttpGet("api/v1/assistant/plugins/catalog/{pluginKey}/workspaces")]
    [ProducesResponseType(typeof(PluginWorkspacesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [RequirePermission(AdminPermissions.PluginsRead)]
    public async Task<IActionResult> ListWorkspaces(string pluginKey, CancellationToken ct) =>
        ToResponse(await _service.GetForPluginAsync(pluginKey, ct));

    /// <summary>The plugin's default: available, opt-in or retired, and which plans it covers.</summary>
    [HttpPut("api/v1/assistant/plugins/catalog/{pluginKey}/availability")]
    [ProducesResponseType(typeof(PluginAvailabilityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [RequirePermission(AdminPermissions.PluginsManage)]
    public async Task<IActionResult> SetAvailability(
        string pluginKey,
        [FromBody] SetPluginAvailabilityRequest request,
        CancellationToken ct) =>
        ToResponse(await _service.SetAvailabilityAsync(pluginKey, request, CurrentUserId, ct));

    /// <summary>Enable, disable or reset the plugin on many workspaces, chosen by id and/or plan.</summary>
    [HttpPost("api/v1/assistant/plugins/catalog/{pluginKey}/workspaces/overrides")]
    [ProducesResponseType(typeof(ApplyPluginOverrideResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [RequirePermission(AdminPermissions.PluginsManage)]
    public async Task<IActionResult> ApplyOverride(
        string pluginKey,
        [FromBody] ApplyPluginOverrideRequest request,
        CancellationToken ct) =>
        ToResponse(await _service.ApplyOverrideAsync(pluginKey, request, CurrentUserId, ct));

    /// <summary>Every marketplace plugin, with its effective state in this workspace.</summary>
    [HttpGet("api/v1/assistant/admin/workspaces/{workspaceId:guid}/plugins")]
    [ProducesResponseType(typeof(WorkspacePluginsAdminDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [RequirePermission(AdminPermissions.PluginsRead)]
    public async Task<IActionResult> ListWorkspacePlugins(Guid workspaceId, CancellationToken ct) =>
        ToResponse(await _service.GetForWorkspaceAsync(workspaceId, ct));

    /// <summary>Enable, disable or reset one plugin in this workspace.</summary>
    [HttpPut("api/v1/assistant/admin/workspaces/{workspaceId:guid}/plugins/{pluginKey}/override")]
    [ProducesResponseType(typeof(PluginWorkspaceRowDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [RequirePermission(AdminPermissions.PluginsManage)]
    public async Task<IActionResult> SetWorkspaceOverride(
        Guid workspaceId,
        string pluginKey,
        [FromBody] SetWorkspacePluginOverrideRequest request,
        CancellationToken ct) =>
        ToResponse(await _service.SetWorkspaceOverrideAsync(workspaceId, pluginKey, request, CurrentUserId, ct));

    public static int StatusFor(string? errorCode) => errorCode switch
    {
        PluginConstants.ErrorCodes.UnknownPlugin
            or PluginWorkspaceAccessConstants.ErrorCodes.UnknownWorkspace => StatusCodes.Status404NotFound,
        WorkspacePluginConstants.ErrorCodes.ListChangedConcurrently => StatusCodes.Status409Conflict,
        // The workspace service or the audit log did not answer: nothing was changed.
        PluginWorkspaceAccessConstants.ErrorCodes.WorkspacesUnavailable
            or WarpTalk.Shared.ErrorCodes.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };

    private IActionResult ToResponse<T>(WarpTalk.Shared.Result<T> result) =>
        result.IsSuccess
            ? Ok(result.Value)
            : StatusCode(StatusFor(result.ErrorCode), new { error = result.Error, errorCode = result.ErrorCode });
}
