using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

/// <summary>
/// The WarpBot tools page: what WarpBot can reach for in one workspace, for the signed-in member.
/// </summary>
[ApiController]
[Route("api/v1/assistant/tools")]
[Authorize]
public class AssistantToolsController : ControllerBase
{
    private readonly IAssistantToolsService _toolsService;
    private readonly IStaffAccessResolver _staffAccess;

    public AssistantToolsController(IAssistantToolsService toolsService, IStaffAccessResolver staffAccess)
    {
        _toolsService = toolsService;
        _staffAccess = staffAccess;
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    /// <param name="workspaceId">The workspace the member is browsing. Required.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(AssistantToolsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTools([FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        if (workspaceId is not { } id || id == Guid.Empty)
            return BadRequest("workspaceId is required.");

        // platform_staff tools (get_platform_analytics) run in the platform WarpBot, which is gated
        // on warpbot.use - so that is who is told about them. Asked only when the token carries the
        // staff hint, so an ordinary member never costs a call to the auth service.
        var callerIsPlatformStaff = await _staffAccess.StaffOverrideAllowsAsync(User, AdminPermissions.WarpBotUse, ct);

        var result = await _toolsService.GetToolsAsync(CurrentUserId, id, callerIsPlatformStaff, ct);
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode == PluginConstants.ErrorCodes.PermissionDenied
            ? StatusCode(StatusCodes.Status403Forbidden, result.Error)
            : BadRequest(result.Error);
    }
}
