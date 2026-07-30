using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

public sealed record AssistantSkillDto(string Name, string Label, string Description);

[ApiController]
[Route("api/v1/assistant")]
[Authorize]
public class AssistantSkillsController : ControllerBase
{
    // Static v1 catalog mirroring the OpenAI tool schemas in
    // warptalk-ai/ai_assistant_worker/chat_tools.py — the chat assistant itself owns
    // the live tool-calling loop, this endpoint only powers the frontend's "Skills"
    // dropdown, so it doesn't need to introspect the Python worker at runtime.
    private static readonly IReadOnlyList<AssistantSkillDto> Skills = new List<AssistantSkillDto>
    {
        new("search_workspace_members", "Search members", "Find teammates in this workspace by name or email."),
        new("search_terminology", "Search terminology", "Look up glossary terms and their translations."),
        new("list_recent_meetings", "Recent meetings", "List your recent translation room meetings."),
        new("translate_text", "Translate text", "Translate a piece of text into another language."),
        new("semantic_search", "Search knowledge base", "Semantically search indexed documents and transcripts."),
        new("get_meeting_summary", "Meeting summary", "Get the AI summary and action items for a past meeting."),
        new("get_room_detail", "Room details", "Get full details for a specific translation room — status, languages, host, schedule."),
        new("get_transcript", "Meeting transcript", "Get the transcribed segments for a specific meeting."),
        new("get_document", "Document details", "Get metadata and a text excerpt for a specific workspace document."),
    };

    [HttpGet("skills")]
    public IActionResult ListSkills() => Ok(Skills);
}

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
        var bearerToken = Request.Headers.Authorization.ToString();
        var result = await _conversationService.SendMessageAsync(id, CurrentUserId, bearerToken, request, ct);
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
