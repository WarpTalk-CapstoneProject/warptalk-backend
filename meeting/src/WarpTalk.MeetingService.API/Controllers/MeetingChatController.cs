using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/v1/meetings/{roomId:guid}/chat")]
[Authorize]
public class MeetingChatController : ControllerBase
{
    private readonly IMeetingChatService _chatService;

    public MeetingChatController(IMeetingChatService chatService)
    {
        _chatService = chatService;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirst("sub")?.Value ?? Guid.Empty.ToString());

    [HttpGet]
    public async Task<IActionResult> GetMessages(Guid roomId, CancellationToken ct)
    {
        var result = await _chatService.GetRoomMessagesAsync(roomId, CurrentUserId, ct);
        if (!result.IsSuccess)
            return BadRequest(result.Error);
            
        return Ok(result.Value);
    }

    [HttpPost]
    public async Task<IActionResult> SendMessage(Guid roomId, [FromBody] SendMeetingChatMessageRequest request, CancellationToken ct)
    {
        var result = await _chatService.SendMessageAsync(roomId, CurrentUserId, request, ct);
        if (!result.IsSuccess)
            return BadRequest(result.Error);
            
        return Ok(result.Value);
    }

    [HttpPost("{messageId:guid}/translate")]
    public async Task<IActionResult> RequestTranslation(Guid roomId, Guid messageId, [FromBody] TranslateMeetingChatMessageRequest request, CancellationToken ct)
    {
        var result = await _chatService.RequestTranslationAsync(roomId, messageId, CurrentUserId, request, ct);
        if (!result.IsSuccess)
            return BadRequest(result.Error);
            
        return Accepted();
    }

    [HttpPost("{messageId:guid}/moderate")]
    public async Task<IActionResult> ModerateMessage(Guid roomId, Guid messageId, [FromBody] ModerateMeetingChatMessageRequest request, CancellationToken ct)
    {
        var result = await _chatService.ModerateMessageAsync(roomId, messageId, CurrentUserId, request, ct);
        if (!result.IsSuccess)
            return BadRequest(result.Error);
            
        return Ok();
    }
}
