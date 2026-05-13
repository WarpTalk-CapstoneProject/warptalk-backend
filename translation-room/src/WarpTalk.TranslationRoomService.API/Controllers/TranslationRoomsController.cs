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

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/v1/translation-rooms")]
[Authorize]
public class TranslationRoomsController : ControllerBase
{
    private readonly ITranslationRoomService _translationRoomService;
    private readonly IValidator<CreateTranslationRoomRequest> _createValidator;

    public TranslationRoomsController(
        ITranslationRoomService translationRoomService,
        IValidator<CreateTranslationRoomRequest> createValidator)
    {
        _translationRoomService = translationRoomService;
        _createValidator = createValidator;
    }

    [HttpPost]
    public async Task<IActionResult> CreateTranslationRoom([FromBody] CreateTranslationRoomRequest request)
    {
        var validationResult = await _createValidator.ValidateAsync(request);
        if (!validationResult.IsValid)
        {
            var errors = validationResult.Errors.Select(e => e.ErrorMessage).ToList();
            return BadRequest(new ApiErrorResponse(string.Join(" ", errors), ErrorCodes.ValidationError));
        }

        // Extract HostId from JWT claims (Assuming NameIdentifier or sub)
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                          ?? User.FindFirst("sub")?.Value;

        if (!Guid.TryParse(userIdClaim, out var hostId))
        {
            return Unauthorized(new ApiErrorResponse(
                ApiMessageConstants.ErrorMessages.UnauthorizedTokenDetail, 
                ErrorCodes.Unauthorized));
        }

        
        var result = await _translationRoomService.CreateTranslationRoomAsync(request, hostId);

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

    [HttpPost("{id}/join")]
    public async Task<IActionResult> JoinTranslationRoom(Guid id, [FromBody] JoinTranslationRoomRequest request, CancellationToken ct)
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                          ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _translationRoomService.JoinTranslationRoomAsync(id, userId, request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value!);
    }

    [HttpPost("{id}/end")]
    public async Task<IActionResult> EndTranslationRoom(Guid id, CancellationToken ct)
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                          ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var hostId))
            return Unauthorized();

        var result = await _translationRoomService.EndTranslationRoomAsync(id, hostId, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }
}
