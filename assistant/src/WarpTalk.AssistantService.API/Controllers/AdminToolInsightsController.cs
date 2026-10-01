using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.AssistantService.API.Controllers;

/// <summary>The platform-wide view of <see cref="WorkspaceToolInsightsController"/>, for the admin WarpBot tools page.</summary>
[ApiController]
[Route("api/v1/assistant/admin/insights/tools")]
public class AdminToolInsightsController : ControllerBase
{
    private readonly IAssistantToolInsightsService _service;

    public AdminToolInsightsController(IAssistantToolInsightsService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminToolInsightsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequirePermission(AdminPermissions.PluginsRead)]
    public async Task<IActionResult> Get(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var result = await _service.GetPlatformAsync(from?.UtcDateTime, to?.UtcDateTime, ct);
        if (result.IsSuccess) return Ok(result.Value);

        return BadRequest(new { error = result.Error, errorCode = result.ErrorCode });
    }
}
