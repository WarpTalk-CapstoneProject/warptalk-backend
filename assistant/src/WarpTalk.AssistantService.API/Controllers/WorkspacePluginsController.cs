using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

/// <summary>
/// The workspace half of the plugin marketplace.
/// </summary>
/// <remarks>
/// Its own prefix, <c>api/v1/assistant/workspaces/{workspaceId}/plugins</c>, rather than more literal
/// segments under <c>api/v1/assistant/plugins</c>: every literal added beside a <c>{pluginKey}</c>
/// route there reserves a plugin key (see <c>plugins_plugin_key_not_reserved</c>). The gateway's
/// <c>/api/v1/assistant/{**catch-all}</c> route already covers this prefix.
/// <para>
/// Authorisation is resolved by the service from the workspace service, per call, against the
/// workspace in the PATH - never from anything else the client sends. Reads of the list and its
/// requests: Owner or Admin. Every write: Owner. Asking for a plugin: any active member.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/assistant/workspaces/{workspaceId:guid}/plugins")]
[Authorize]
public class WorkspacePluginsController : ControllerBase
{
    private readonly IWorkspacePluginMarketplaceService _service;

    public WorkspacePluginsController(IWorkspacePluginMarketplaceService service)
    {
        _service = service;
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    /// <summary>The workspace's plugins, marketplace candidates and pending requests. Owner or Admin.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(WorkspacePluginsOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOverview(Guid workspaceId, CancellationToken ct) =>
        ToResponse(await _service.GetOverviewAsync(workspaceId, CurrentUserId, User.GetEmail(), ct));

    /// <summary>Adds a marketplace plugin. Owner.</summary>
    [HttpPost("marketplace/{pluginKey}")]
    [ProducesResponseType(typeof(WorkspacePluginItemDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> AddMarketplacePlugin(Guid workspaceId, string pluginKey, CancellationToken ct) =>
        ToResponse(await _service.AddMarketplacePluginAsync(workspaceId, CurrentUserId, pluginKey, ct));

    /// <summary>Removes a plugin from the workspace; a private plugin is retired. Owner.</summary>
    [HttpDelete("{pluginKey}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemovePlugin(Guid workspaceId, string pluginKey, CancellationToken ct)
    {
        var result = await _service.RemovePluginAsync(workspaceId, CurrentUserId, pluginKey, ct);
        return result.IsSuccess ? NoContent() : Error(result.Error, result.ErrorCode);
    }

    /// <summary>Creates a private MCP plugin visible only in this workspace. Owner.</summary>
    [HttpPost("private")]
    [ProducesResponseType(typeof(WorkspacePluginItemDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreatePrivatePlugin(
        Guid workspaceId,
        [FromBody] CreatePrivatePluginRequest request,
        CancellationToken ct) =>
        ToResponse(await _service.CreatePrivatePluginAsync(workspaceId, CurrentUserId, request, ct));

    /// <summary>Edits this workspace's private plugin. Owner.</summary>
    [HttpPatch("private/{pluginKey}")]
    [ProducesResponseType(typeof(WorkspacePluginItemDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePrivatePlugin(
        Guid workspaceId,
        string pluginKey,
        [FromBody] UpdatePrivatePluginRequest request,
        CancellationToken ct) =>
        ToResponse(await _service.UpdatePrivatePluginAsync(workspaceId, CurrentUserId, pluginKey, request, ct));

    /// <summary>Pending requests. Owner or Admin.</summary>
    [HttpGet("requests")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkspacePluginRequestDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPendingRequests(Guid workspaceId, CancellationToken ct) =>
        ToResponse(await _service.ListPendingRequestsAsync(workspaceId, CurrentUserId, ct));

    /// <summary>The caller's own requests in this workspace. Any active member.</summary>
    [HttpGet("requests/mine")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkspacePluginRequestDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMyRequests(Guid workspaceId, CancellationToken ct) =>
        ToResponse(await _service.ListMyRequestsAsync(workspaceId, CurrentUserId, ct));

    /// <summary>Asks the workspace Owner to add a marketplace plugin. Any active member.</summary>
    [HttpPost("requests")]
    [ProducesResponseType(typeof(WorkspacePluginRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateRequest(
        Guid workspaceId,
        [FromBody] CreatePluginRequestRequest request,
        CancellationToken ct) =>
        ToResponse(await _service.CreateRequestAsync(workspaceId, CurrentUserId, User.GetEmail(), request, ct));

    /// <summary>Adds the plugin and answers every pending request for it. Owner.</summary>
    [HttpPost("requests/{requestId:guid}/approve")]
    [ProducesResponseType(typeof(WorkspacePluginRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ApproveRequest(Guid workspaceId, Guid requestId, CancellationToken ct) =>
        ToResponse(await _service.ApproveRequestAsync(workspaceId, CurrentUserId, requestId, ct));

    /// <summary>Declines one request. Owner.</summary>
    [HttpPost("requests/{requestId:guid}/decline")]
    [ProducesResponseType(typeof(WorkspacePluginRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeclineRequest(Guid workspaceId, Guid requestId, CancellationToken ct) =>
        ToResponse(await _service.DeclineRequestAsync(workspaceId, CurrentUserId, requestId, ct));

    private IActionResult ToResponse<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);

    public static int StatusFor(string? errorCode) => errorCode switch
    {
        PluginConstants.ErrorCodes.PermissionDenied => StatusCodes.Status403Forbidden,
        PluginConstants.ErrorCodes.UnknownPlugin
            or WorkspacePluginConstants.ErrorCodes.UnknownRequest
            or WorkspacePluginConstants.ErrorCodes.NotAPrivatePlugin => StatusCodes.Status404NotFound,
        WorkspacePluginConstants.ErrorCodes.RequestAlreadyPending
            or WorkspacePluginConstants.ErrorCodes.RequestNotPending
            or WorkspacePluginConstants.ErrorCodes.PluginAlreadyAvailable
            or WorkspacePluginConstants.ErrorCodes.PluginRetired
            or WorkspacePluginConstants.ErrorCodes.ListChangedConcurrently
            or WorkspacePluginConstants.ErrorCodes.RequestByOwner => StatusCodes.Status409Conflict,
        // The workspace service did not answer, so nothing was written: the page should say "try
        // again", not "you did something wrong".
        WorkspacePluginConstants.ErrorCodes.PolicyUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };

    private IActionResult Error(string? message, string? errorCode) =>
        StatusCode(StatusFor(errorCode), new { error = message, errorCode });
}
