using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// Platform-wide meeting directory for the System Admin portal.
///
/// Metadata only, and read-only. An administrator can see that a meeting ran, for how long, in
/// which languages and how it ended — and cannot join it, control it, or read a word of what was
/// said. That boundary is the reason there is no "open room" action anywhere on this controller.
/// </summary>
[ApiController]
[Route("api/v1/admin/meetings")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminMeetingsController : ControllerBase
{
    private readonly IAdminMeetingService _adminMeetingService;

    public AdminMeetingsController(IAdminMeetingService adminMeetingService)
    {
        _adminMeetingService = adminMeetingService;
    }

    [HttpGet]
    public async Task<IActionResult> GetDirectory(
        [FromQuery] AdminMeetingDirectoryQuery query,
        CancellationToken ct)
    {
        var result = await _adminMeetingService.GetDirectoryAsync(query, ct);
        return ToActionResult(result);
    }

    /// <summary>
    /// Live and started-today, read together at one instant so the two cannot disagree with each
    /// other the way two separate requests would.
    /// </summary>
    [HttpGet("counts")]
    public async Task<IActionResult> GetCounts(CancellationToken ct)
    {
        var result = await _adminMeetingService.GetCountsAsync(ct);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
