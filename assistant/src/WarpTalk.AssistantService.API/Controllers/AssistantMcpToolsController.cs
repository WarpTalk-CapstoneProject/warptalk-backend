using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

[ApiController]
[Route("api/v1/assistant/mcp/tools")]
[Authorize]
public class AssistantMcpToolsController : ControllerBase
{
    private readonly IMcpToolOrchestrator _orchestrator;
    private readonly IPluginToolAuditQueryService _auditQueryService;

    public AssistantMcpToolsController(
        IMcpToolOrchestrator orchestrator,
        IPluginToolAuditQueryService auditQueryService)
    {
        _orchestrator = orchestrator;
        _auditQueryService = auditQueryService;
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<McpToolDescriptorDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListTools([FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        var result = await _orchestrator.ListAvailableToolsAsync(CurrentUserId, workspaceId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("execute")]
    [ProducesResponseType(typeof(McpToolExecutionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(McpToolExecutionErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(McpToolExecutionErrorDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Execute([FromBody] McpToolExecutionRequest request, CancellationToken ct)
    {
        var result = await _orchestrator.ExecuteAsync(CurrentUserId, request, ct);
        if (result.IsSuccess) return Ok(result.Value);

        var status = result.ErrorCode switch
        {
            PluginConstants.ErrorCodes.UnknownPlugin or PluginConstants.ErrorCodes.UnknownTool => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };

        return StatusCode(
            status,
            new McpToolExecutionErrorDto(result.Error ?? "Plugin tool failed.", result.ErrorCode));
    }

    /// <summary>
    /// What plugin tools have been run in one workspace. WT-646.
    /// </summary>
    /// <remarks>
    /// Sits beside the execute endpoint that writes these rows, and is workspace-scoped by query
    /// parameter exactly as <c>ListTools</c> above is - the audits are a record of what that
    /// endpoint did in that workspace.
    /// <para>
    /// The workspace's own Owner or Admin, not a system admin: authorisation is a role check
    /// against the named workspace, so an ordinary member of it is refused just as firmly as an
    /// outsider. The system-admin view, scoped to one plugin across every workspace, is a
    /// different endpoint with a different audience.
    /// </para>
    /// </remarks>
    /// <param name="workspaceId">The workspace whose usage is being read.</param>
    /// <param name="pluginKey">Optional. Narrows to one plugin.</param>
    /// <param name="userId">Optional. Narrows to one member.</param>
    /// <param name="skip">Rows to skip, newest first.</param>
    /// <param name="take">Page size. Zero or less takes the default; oversized pages are clamped.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("audits")]
    [ProducesResponseType(typeof(IReadOnlyList<PluginToolAuditDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListWorkspaceAudits(
        [FromQuery] Guid workspaceId,
        [FromQuery] string? pluginKey,
        [FromQuery] Guid? userId,
        [FromQuery] int skip,
        [FromQuery] int take,
        CancellationToken ct)
    {
        var result = await _auditQueryService.ListWorkspaceAuditsAsync(
            workspaceId, CurrentUserId, pluginKey, userId, skip, take, ct);

        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode == PluginConstants.ErrorCodes.PermissionDenied
            ? StatusCode(StatusCodes.Status403Forbidden, result.Error)
            : BadRequest(result.Error);
    }
}
