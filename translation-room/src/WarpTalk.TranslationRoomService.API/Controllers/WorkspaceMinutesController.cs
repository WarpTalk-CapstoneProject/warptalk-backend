using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Models;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// The workspace's biên bản, as a list.
///
/// Separate from <see cref="MeetingMinutesController"/> because the route is anchored on a
/// workspace rather than on a room, and a controller cannot hold two route templates. The
/// authority is the same and lives in one place: <c>IMeetingMinutesService</c> scopes the list by
/// the rooms this caller may read, so the list can never show a document the per-room route would
/// refuse.
/// </summary>
[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/minutes")]
[Authorize]
public class WorkspaceMinutesController : ControllerBase
{
    private readonly IMeetingMinutesService _minutesService;

    public WorkspaceMinutesController(IMeetingMinutesService minutesService)
    {
        _minutesService = minutesService;
    }

    /// <summary>Current minutes across the workspace, newest meeting first.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        Guid workspaceId,
        [FromQuery] GetWorkspaceMinutesRequest request,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _minutesService.ListForWorkspaceAsync(
            workspaceId, request, userId.Value, User.GetEmail(), ct);

        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode))
        };
    }
}
