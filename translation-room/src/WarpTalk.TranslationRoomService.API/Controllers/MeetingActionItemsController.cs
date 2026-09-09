using System;
using System.Collections.Generic;
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
/// The work a meeting produced — readable where the meeting is, and closeable by the person it
/// was given to.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public class MeetingActionItemsController : ControllerBase
{
    private readonly IMeetingActionItemService _actionItems;

    public MeetingActionItemsController(IMeetingActionItemService actionItems)
    {
        _actionItems = actionItems;
    }

    [HttpGet("rooms/{roomId}/action-items")]
    public async Task<IActionResult> GetForRoom(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _actionItems.GetForRoomAsync(roomId, userId.Value, User.GetEmail(), ct);
        return result.IsSuccess ? Ok(result.Value) : Fail(result.Error, result.ErrorCode);
    }

    /// <summary>Everything assigned to the caller in one workspace.</summary>
    [HttpGet("workspaces/{workspaceId}/action-items/mine")]
    public async Task<IActionResult> GetMine(
        Guid workspaceId,
        [FromQuery] string? status,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _actionItems.GetMineAsync(workspaceId, userId.Value, status, ct);
        return result.IsSuccess ? Ok(result.Value) : Fail(result.Error, result.ErrorCode);
    }

    [HttpPut("action-items/{itemId}/status")]
    public async Task<IActionResult> UpdateStatus(
        Guid itemId,
        [FromBody] UpdateActionItemStatusRequest request,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request?.Status))
        {
            return BadRequest(new ApiErrorResponse("A status is required.", ErrorCodes.ValidationError));
        }

        var result = await _actionItems.UpdateStatusAsync(
            itemId, userId.Value, request.Status, request.DueDate, ct);

        return result.IsSuccess ? Ok(result.Value) : Fail(result.Error, result.ErrorCode);
    }

    private IActionResult Fail(string? error, string? errorCode) => errorCode switch
    {
        ErrorCodes.NotFound => NotFound(new ApiErrorResponse(error, errorCode)),
        ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(error, errorCode)),
        ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(error, errorCode)),
        _ => StatusCode(500, new ApiErrorResponse(error, errorCode))
    };
}
