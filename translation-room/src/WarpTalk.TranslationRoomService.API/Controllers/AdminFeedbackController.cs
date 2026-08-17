using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// Product feedback across the platform, for the System Admin portal.
///
/// Read-only: no POST, PUT, PATCH or DELETE. A rating an administrator can edit or remove is not
/// a quality signal, and the one write path that exists — a participant rating a meeting they
/// were in — is on the ordinary rooms controller and stays there.
/// </summary>
[ApiController]
[Route("api/v1/admin/feedback")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminFeedbackController : ControllerBase
{
    private readonly IAdminFeedbackService _adminFeedbackService;

    public AdminFeedbackController(IAdminFeedbackService adminFeedbackService)
    {
        _adminFeedbackService = adminFeedbackService;
    }

    /// <summary>Totals, response rate and the five rating dimensions over the window.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] AdminFeedbackQuery query, CancellationToken ct)
        => ToActionResult(await _adminFeedbackService.GetSummaryAsync(query, ct));

    /// <summary>
    /// The free-text comments, newest first. Returned without the person who wrote them.
    /// </summary>
    [HttpGet("comments")]
    public async Task<IActionResult> GetComments([FromQuery] AdminFeedbackQuery query, CancellationToken ct)
        => ToActionResult(await _adminFeedbackService.GetCommentsAsync(query, ct));

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
