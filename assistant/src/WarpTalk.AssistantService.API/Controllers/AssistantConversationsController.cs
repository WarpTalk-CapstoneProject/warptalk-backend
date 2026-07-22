using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

[ApiController]
[Route("api/v1/assistant/conversations")]
[Authorize]
public class AssistantConversationsController : ControllerBase
{
    private readonly IAssistantConversationService _conversationService;

    public AssistantConversationsController(IAssistantConversationService conversationService)
    {
        _conversationService = conversationService;
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    [HttpGet]
    public async Task<IActionResult> ListConversations([FromQuery] Guid workspaceId, CancellationToken ct)
    {
        var result = await _conversationService.ListConversationsAsync(workspaceId, CurrentUserId, ct);
        if (!result.IsSuccess) return BadRequest(result.Error);
        return Ok(result.Value);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetConversation(Guid id, CancellationToken ct)
    {
        var result = await _conversationService.GetConversationAsync(id, CurrentUserId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND") return NotFound(result.Error);
            return BadRequest(result.Error);
        }
        return Ok(result.Value);
    }

    [HttpPost]
    public async Task<IActionResult> CreateConversation([FromBody] CreateAssistantConversationRequest request, CancellationToken ct)
    {
        var result = await _conversationService.CreateConversationAsync(request.WorkspaceId, CurrentUserId, ct);
        if (!result.IsSuccess) return BadRequest(result.Error);
        return Ok(result.Value);
    }

    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> SendMessage(Guid id, [FromBody] SendAssistantMessageRequest request, CancellationToken ct)
    {
        var result = await _conversationService.SendMessageAsync(id, CurrentUserId, request, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND") return NotFound(result.Error);
            if (result.ErrorCode == "VALIDATION_ERROR") return BadRequest(result.Error);
            return BadRequest(result.Error);
        }

        return Accepted(result.Value);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> ArchiveConversation(Guid id, CancellationToken ct)
    {
        var result = await _conversationService.ArchiveConversationAsync(id, CurrentUserId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND") return NotFound(result.Error);
            return BadRequest(result.Error);
        }
        return Ok();
    }
}
