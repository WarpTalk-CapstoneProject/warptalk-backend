using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/v1/meetings/rooms/{roomId:guid}/chat")]
[Authorize]
public class MeetingChatController : ControllerBase
{
    private readonly IMeetingChatService _chatService;

    public MeetingChatController(IMeetingChatService chatService)
    {
        _chatService = chatService;
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    /// <summary>
    /// WT-365 — a 403 that says why.
    ///
    /// Every one of these branches used to call <c>Forbid()</c>. On an API controller that runs
    /// the authentication scheme's forbid handler and returns 403 with NO BODY AT ALL — so the
    /// service's own reason ("Not an active participant.") was computed, returned up the stack,
    /// and then thrown away one line before it reached the wire. The chat panel could only fall
    /// back to "Message could not be sent. Try again.", which is advice that will never work:
    /// the send is being refused, not dropped. That generic sentence is what the bug report
    /// contains, because it is all the user had.
    ///
    /// The sibling NOT_FOUND/BadRequest branches already pass <c>result.Error</c>; this brings
    /// 403 in line with them and with <c>ApiErrorResponse</c>, which is the shape the web
    /// client's getErrorMessage() reads.
    /// </summary>
    private ObjectResult ForbiddenWithReason(string? reason) =>
        StatusCode(
            StatusCodes.Status403Forbidden,
            new ApiErrorResponse(reason ?? "You cannot do that in this meeting.", "FORBIDDEN"));

    [HttpGet]
    public async Task<IActionResult> GetMessages(Guid roomId, CancellationToken ct)
    {
        var result = await _chatService.GetRoomMessagesAsync(roomId, CurrentUserId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND") return NotFound(result.Error);
            if (result.ErrorCode == "FORBIDDEN") return ForbiddenWithReason(result.Error);
            return BadRequest(result.Error);
        }

        return Ok(result.Value);
    }

    [HttpPost]
    public async Task<IActionResult> SendMessage(Guid roomId, [FromBody] SendMeetingChatMessageRequest request, CancellationToken ct)
    {
        var bearerToken = Request.Headers.Authorization.ToString();
        var result = await _chatService.SendMessageAsync(roomId, CurrentUserId, request, bearerToken, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND") return NotFound(result.Error);
            if (result.ErrorCode == "FORBIDDEN") return ForbiddenWithReason(result.Error);
            return BadRequest(result.Error);
        }

        return Ok(result.Value);
    }

    [HttpPost("{messageId:guid}/translate")]
    public async Task<IActionResult> RequestTranslation(Guid roomId, Guid messageId, [FromBody] TranslateMeetingChatMessageRequest request, CancellationToken ct)
    {
        var result = await _chatService.RequestTranslationAsync(roomId, messageId, CurrentUserId, request, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND") return NotFound(result.Error);
            if (result.ErrorCode == "FORBIDDEN") return ForbiddenWithReason(result.Error);
            return BadRequest(result.Error);
        }

        return Ok(result.Value);
    }

    [HttpPost("{messageId:guid}/moderate")]
    public async Task<IActionResult> ModerateMessage(Guid roomId, Guid messageId, [FromBody] ModerateMeetingChatMessageRequest request, CancellationToken ct)
    {
        var result = await _chatService.ModerateMessageAsync(roomId, messageId, CurrentUserId, request, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND") return NotFound(result.Error);
            if (result.ErrorCode == "FORBIDDEN") return ForbiddenWithReason(result.Error);
            return BadRequest(result.Error);
        }

        return Ok();
    }

    [HttpPost("files")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> UploadFile(Guid roomId, [FromForm] UploadMeetingChatFileRequest request, CancellationToken ct)
    {
        var result = await _chatService.UploadFileAsync(roomId, CurrentUserId, request, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND") return NotFound(result.Error);
            if (result.ErrorCode == "FORBIDDEN") return ForbiddenWithReason(result.Error);
            return BadRequest(result.Error);
        }

        return Ok(result.Value);
    }

    [HttpGet("files/{messageId:guid}/download")]
    public async Task<IActionResult> DownloadFile(Guid roomId, Guid messageId, CancellationToken ct)
    {
        var result = await _chatService.DownloadFileAsync(roomId, messageId, CurrentUserId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND") return NotFound(result.Error);
            if (result.ErrorCode == "FORBIDDEN") return ForbiddenWithReason(result.Error);
            return BadRequest(result.Error);
        }

        return File(result.Value!.Stream, result.Value.ContentType, result.Value.FileName);
    }
}
