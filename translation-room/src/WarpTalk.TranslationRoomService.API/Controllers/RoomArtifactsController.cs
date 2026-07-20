using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Models;
using WarpTalk.Shared.Extensions;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/v1/room-artifacts")]
[Authorize]
public class RoomArtifactsController : ControllerBase
{
    private readonly ITranslationRoomArtifactService _artifactService;

    public RoomArtifactsController(ITranslationRoomArtifactService artifactService)
    {
        _artifactService = artifactService;
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadArtifact(Guid id, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _artifactService.GetArtifactDownloadUrlAsync(id, userId.Value, ct);
        
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        // TODO: In production, this should generate a dynamic Presigned URL with expiration from S3 Cloud/CDN and Redirect to it.
        // Temporarily returning the static URL as a JSON response to synchronize with the current TranslationRoomArtifactService implementation.
        return Ok(new { Url = result.Value! });
    }

    [HttpPost("{id}/consent")]
    public async Task<IActionResult> ApproveConsent(Guid id, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _artifactService.ApproveArtifactConsentAsync(id, userId.Value, ct);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return NoContent();
    }
}
