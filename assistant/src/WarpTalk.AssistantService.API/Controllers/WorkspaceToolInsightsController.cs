using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

/// <summary>
/// Every WarpBot tool call - built-in, web search and plugin - counted from
/// <c>assistant_tool_calls</c> (wave 4). Metadata only: no argument or result text exists to return.
/// </summary>
/// <remarks>
/// Two audiences, two controllers, so no action decides which gate applies: the workspace's own
/// Owner or Admin (resolved by the service against the workspace in the PATH, the same check as the
/// plugin audits), and platform staff with <c>plugins.read</c>, as on the other admin plugin pages.
/// <para>
/// <c>from</c> / <c>to</c> are ISO instants, UTC. Defaults: the last 30 days. A longer window than
/// 180 days is clamped to its last 180; the response's <c>from</c> / <c>to</c> are the window used.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/assistant/workspaces/{workspaceId:guid}/insights/tools")]
[Authorize]
public class WorkspaceToolInsightsController : ControllerBase
{
    private readonly IAssistantToolInsightsService _service;

    public WorkspaceToolInsightsController(IAssistantToolInsightsService service)
    {
        _service = service;
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    [HttpGet]
    [ProducesResponseType(typeof(WorkspaceToolInsightsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(
        Guid workspaceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var result = await _service.GetWorkspaceAsync(workspaceId, CurrentUserId, from?.UtcDateTime, to?.UtcDateTime, ct);
        if (result.IsSuccess) return Ok(result.Value);

        var body = new { error = result.Error, errorCode = result.ErrorCode };
        return result.ErrorCode == PluginConstants.ErrorCodes.PermissionDenied
            ? StatusCode(StatusCodes.Status403Forbidden, body)
            : BadRequest(body);
    }
}
