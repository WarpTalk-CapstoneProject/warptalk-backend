using System;
using System.Security.Claims;
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
/// One shared EXTERNAL_BRIDGE room per Google Meet code, with a single audio capturer. See
/// TranslationRoomService.Bridge for the rules.
/// </summary>
[ApiController]
[Route("api/v1/translation-rooms")]
[Authorize]
public class BridgeRoomsController : ControllerBase
{
    private readonly ITranslationRoomService _translationRoomService;

    public BridgeRoomsController(ITranslationRoomService translationRoomService)
    {
        _translationRoomService = translationRoomService;
    }

    /// <summary>
    /// Find-or-create this Meet call's room in the workspace, join it, and learn whether this
    /// desktop captures the far side (<c>bridgeRole: "capturer"</c>) or only publishes its mic
    /// (<c>"member"</c>). 200 whether the room was created or reused (<c>created</c> says which).
    /// </summary>
    [HttpPost("bridge/claim")]
    public async Task<IActionResult> Claim([FromBody] ClaimBridgeRoomRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        // Creating a room requires a verified email on POST /translation-rooms; claim may create.
        if (!User.IsEmailVerified())
            return StatusCode(403, new ApiErrorResponse("Email not verified", ErrorCodes.AccountPending));

        var result = await _translationRoomService.ClaimBridgeRoomAsync(
            request, userId.Value, User.FindFirstValue(ClaimTypes.Email), ct);

        return result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);
    }

    /// <summary>The capturer renews its lease. 409 when the caller is not (or no longer) the capturer.</summary>
    [HttpPost("{id:guid}/bridge/capturer/heartbeat")]
    public async Task<IActionResult> Heartbeat(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.HeartbeatBridgeCapturerAsync(id, userId.Value, ct);
        return result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);
    }

    /// <summary>
    /// A participant takes over capturing when there is no capturer or its lease is stale.
    /// 409 while a live capturer holds it.
    /// </summary>
    [HttpPost("{id:guid}/bridge/capturer/takeover")]
    public async Task<IActionResult> TakeOver(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.TakeOverBridgeCapturerAsync(id, userId.Value, ct);
        return result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);
    }

    /// <summary>
    /// ApiErrorStatus, except INVALID_STATE (not a bridge / room already ended), which these
    /// endpoints report as 409: the request is well-formed, the room is just past the point where it
    /// can be captured.
    /// </summary>
    private ObjectResult Error(string? error, string? errorCode)
    {
        var status = errorCode == ErrorCodes.InvalidState ? 409 : ApiErrorStatus.For(errorCode);
        return StatusCode(status, new ApiErrorResponse(error, errorCode));
    }
}
