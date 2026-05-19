using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using FluentValidation;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/v1/translation-rooms")]
[Authorize]
public class TranslationRoomsController : ControllerBase
{
    private readonly ITranslationRoomService _translationRoomService;

    public TranslationRoomsController(
        ITranslationRoomService translationRoomService)
    {
        _translationRoomService = translationRoomService;
    }

    [HttpGet]
    public async Task<IActionResult> GetTranslationRooms([FromQuery] GetTranslationRoomsRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetTranslationRoomsAsync(request, userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value!);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTranslationRoom([FromBody] CreateTranslationRoomRequest request)
    {
        var hostId = User.GetUserId();
        if (hostId == null)
        {
            return Unauthorized();
        }

        var result = await _translationRoomService.CreateTranslationRoomAsync(request, hostId.Value);

        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return CreatedAtAction(nameof(CreateTranslationRoom), new { id = result.Value!.Id }, result.Value);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTranslationRoom(Guid id, CancellationToken ct)
    {
        var result = await _translationRoomService.GetTranslationRoomAsync(id, ct);
        if (!result.IsSuccess)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value!);
    }

    [HttpPost("join")]
    public async Task<IActionResult> JoinTranslationRoom([FromBody] JoinTranslationRoomRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.JoinTranslationRoomAsync(request, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    [HttpPost("{id}/end")]
    public async Task<IActionResult> EndTranslationRoom(Guid id, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null)
            return Unauthorized();

        var result = await _translationRoomService.EndTranslationRoomAsync(id, hostId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }

    [HttpPost("{id}/start")]
    public async Task<IActionResult> StartTranslationRoom(Guid id, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null)
            return Unauthorized();

        var result = await _translationRoomService.StartTranslationRoomAsync(id, hostId.Value, ct);
        if (!result.IsSuccess)
            return ToActionError(result);

        return Ok(result.Value!);
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelTranslationRoom(Guid id, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null)
            return Unauthorized();

        var result = await _translationRoomService.CancelTranslationRoomAsync(id, hostId.Value, ct);
        if (!result.IsSuccess)
            return ToActionError(result);

        return Ok(result.Value!);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetTranslationRoomHistory([FromQuery] GetTranslationRoomsRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetTranslationRoomHistoryAsync(request, userId.Value, ct);
        if (!result.IsSuccess)
            return ToActionError(result);

        return Ok(result.Value!);
    }

    [HttpGet("{id}/artifacts")]
    public async Task<IActionResult> GetTranslationRoomArtifacts(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetTranslationRoomArtifactsAsync(id, userId.Value, ct);
        if (!result.IsSuccess)
            return ToActionError(result);

        return Ok(result.Value!);
    }

    [HttpGet("{id}/feedback/me")]
    public async Task<IActionResult> GetMyFeedback(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetFeedbackStateAsync(id, userId.Value, ct);
        if (!result.IsSuccess)
            return ToActionError(result);

        return Ok(result.Value!);
    }

    [HttpPost("{id}/feedback")]
    public async Task<IActionResult> SubmitFeedback(Guid id, [FromBody] SubmitTranslationRoomFeedbackRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.SubmitFeedbackAsync(id, userId.Value, request, ct);
        if (!result.IsSuccess)
            return ToActionError(result);

        return CreatedAtAction(nameof(GetMyFeedback), new { id }, result.Value!);
    }
//Chua co enpoint PATCH nen tach rieng settings
    [HttpPut("{id}/settings")]
    public async Task<IActionResult> UpdateTranslationRoomSettings(Guid id, [FromBody] UpdateRoomSettingsRequest request, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null)
            return Unauthorized();

        var result = await _translationRoomService.UpdateTranslationRoomSettingsAsync(id, hostId.Value, request, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return NoContent();
    }

    private IActionResult ToActionError<T>(Result<T> result)
    {
        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Unauthorized => Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.InvalidState => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode))
        };
    }

    private IActionResult ToActionError(Result result)
    {
        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Unauthorized => Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.InvalidState => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode))
        };
    }
}
