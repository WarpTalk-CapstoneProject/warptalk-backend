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

    public AssistantMcpToolsController(IMcpToolOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
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
}
