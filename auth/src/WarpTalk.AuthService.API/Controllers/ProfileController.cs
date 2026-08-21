using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class ProfileController : ControllerBase
{
    private readonly IProfileService _profileService;

    public ProfileController(IProfileService profileService)
    {
        _profileService = profileService;
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _profileService.GetProfileAsync(userId.Value, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.NotFound || result.ErrorCode == ErrorCodes.UserNotFound)
            {
                return NotFound(errorResponse);
            }
            return BadRequest(errorResponse);
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Replace this user's avatar. Multipart, because the browser is uploading a file.
    ///
    /// The 2 MB request cap is stated here as well as in the service: RequestSizeLimit refuses an
    /// oversized body before it is buffered, while the service's check is what answers with a
    /// sentence a person can read.
    /// </summary>
    [Authorize]
    [HttpPost("profile/avatar")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> UploadAvatar(IFormFile file, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();
        if (file is null) return BadRequest(new ApiErrorResponse("No image was uploaded.", ErrorCodes.ValidationError));

        await using var stream = file.OpenReadStream();
        var result = await _profileService.UpdateAvatarAsync(
            userId.Value, stream, file.ContentType, file.Length, ct);

        if (!result.IsSuccess)
        {
            var error = new ApiErrorResponse(result.Error, result.ErrorCode);
            return result.ErrorCode is ErrorCodes.NotFound or ErrorCodes.UserNotFound
                ? NotFound(error)
                : BadRequest(error);
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// The avatar itself.
    ///
    /// Anonymous, and it has to be: an &lt;img&gt; tag sends no Authorization header, so an
    /// authenticated route here would mean every avatar in the product rendering as a broken
    /// image. What it exposes is a picture of somebody's face, addressed by their own user id,
    /// to anyone who already has that id — which is everyone who can see them in a meeting.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("profile/avatar/{fileName}")]
    public async Task<IActionResult> GetAvatar(string fileName, CancellationToken ct)
    {
        var separator = fileName?.LastIndexOf('.') ?? -1;
        if (fileName is null || separator <= 0)
        {
            return NotFound();
        }

        if (!Guid.TryParse(fileName[..separator], out var userId))
        {
            return NotFound();
        }

        var result = await _profileService.GetAvatarAsync(userId, fileName[(separator + 1)..], ct);
        if (!result.IsSuccess || result.Value is null)
        {
            return NotFound();
        }

        // No-store rather than a long cache: the URL does not change when somebody replaces
        // their picture, so a cached copy is how a new avatar stays invisible for a day.
        Response.Headers.CacheControl = "no-store";
        return File(result.Value.Content, result.Value.ContentType);
    }

    [Authorize]
    [HttpPut("me")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _profileService.UpdateProfileAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.NotFound || result.ErrorCode == ErrorCodes.UserNotFound)
            {
                return NotFound(errorResponse);
            }
            return BadRequest(errorResponse);
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _profileService.ChangePasswordAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.InvalidCredentials || result.ErrorCode == ErrorCodes.Unauthorized)
            {
                return Unauthorized(errorResponse);
            }
            return BadRequest(errorResponse);
        }
        return NoContent();
    }
}
