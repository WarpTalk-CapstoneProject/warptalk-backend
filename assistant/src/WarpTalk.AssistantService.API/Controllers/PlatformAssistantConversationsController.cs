using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

/// <summary>
/// Platform-scope WarpBot — a system administrator's assistant in the admin portal.
///
/// THE GATE IS HERE, SERVER-SIDE. The whole controller sits behind the WarpTalkSystemAdmin policy
/// (the exact platform role "admin", not the workspace "Admin"), so a workspace member — or a
/// workspace Owner — gets a 403 before any conversation is read, created or sent to. The web
/// widget opening in platform mode only on /admin is presentation; this attribute is the rule.
///
/// The worker's tools then call the admin APIs with the caller's own token, each behind the same
/// policy, so an admin demoted mid-conversation loses the data on the very next tool call too.
/// </summary>
[ApiController]
[Route("api/v1/assistant/platform/conversations")]
[RequirePermission(AdminPermissions.WarpBotUse)]
public class PlatformAssistantConversationsController : ControllerBase
{
    private readonly IPlatformAssistantConversationService _conversationService;

    public PlatformAssistantConversationsController(IPlatformAssistantConversationService conversationService)
    {
        _conversationService = conversationService;
    }

    private Guid CurrentUserId => User.GetUserId() ?? Guid.Empty;

    [HttpGet]
    public async Task<IActionResult> ListConversations(CancellationToken ct)
    {
        var result = await _conversationService.ListConversationsAsync(CurrentUserId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetConversation(Guid id, CancellationToken ct)
    {
        var result = await _conversationService.GetConversationAsync(id, CurrentUserId, ct);
        if (result.IsSuccess) return Ok(result.Value);
        return result.ErrorCode == ErrorCodes.NotFound ? NotFound(result.Error) : BadRequest(result.Error);
    }

    [HttpPost]
    public async Task<IActionResult> CreateConversation([FromBody] CreatePlatformConversationRequest? request, CancellationToken ct)
    {
        var result = await _conversationService.CreateConversationAsync(
            CurrentUserId, request ?? new CreatePlatformConversationRequest(), ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> SendMessage(Guid id, [FromBody] SendPlatformMessageRequest request, CancellationToken ct)
    {
        var bearerToken = Request.Headers.Authorization.ToString();
        var result = await _conversationService.SendMessageAsync(id, CurrentUserId, bearerToken, request, ct);
        if (result.IsSuccess) return Accepted(result.Value);
        return result.ErrorCode == ErrorCodes.NotFound ? NotFound(result.Error) : BadRequest(result.Error);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> ArchiveConversation(Guid id, CancellationToken ct)
    {
        var result = await _conversationService.ArchiveConversationAsync(id, CurrentUserId, ct);
        if (result.IsSuccess) return Ok();
        return result.ErrorCode == ErrorCodes.NotFound ? NotFound(result.Error) : BadRequest(result.Error);
    }
}
