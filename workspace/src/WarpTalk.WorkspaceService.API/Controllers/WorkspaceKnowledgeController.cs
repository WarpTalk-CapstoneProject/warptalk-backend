using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceKnowledge;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// What the system has indexed about this workspace — the chunk text that was embedded and
/// the fact extracted from each — for the workspace Owner/Admin.
///
/// The workspace comes from the route and the caller from the token; the service checks the
/// two against workspace_members on every call.
///
/// READING AND WRITING HAVE DIFFERENT BARS. Owner and Admin can both see the listing. Only the
/// Owner can correct a fact or delete a chunk: those decide what the assistant will tell
/// everyone in the workspace, and what evidence remains of what it was told.
/// </summary>
[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/knowledge")]
public class WorkspaceKnowledgeController : ControllerBase
{
    private readonly IWorkspaceKnowledgeService _knowledgeService;

    public WorkspaceKnowledgeController(IWorkspaceKnowledgeService knowledgeService)
    {
        _knowledgeService = knowledgeService;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetKnowledge(
        Guid workspaceId,
        [FromQuery] GetWorkspaceKnowledgeQuery query,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _knowledgeService.GetKnowledgeAsync(workspaceId, query, userId.Value, ct);
        return ToActionResult(result);
    }

    /// <summary>
    /// Corrects one chunk's fact, its category, or whether WarpBot may retrieve it.
    ///
    /// PATCH and not PUT: this replaces the three annotation fields and nothing else — the
    /// indexed text and the provenance are not the caller's to send, so a body that looked
    /// like the whole resource would be inviting them to try.
    /// </summary>
    [Authorize]
    [HttpPatch("{chunkId}")]
    public async Task<IActionResult> UpdateChunk(
        Guid workspaceId,
        string chunkId,
        [FromBody] UpdateWorkspaceKnowledgeChunkRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _knowledgeService.UpdateKnowledgeChunkAsync(
            workspaceId, chunkId, request, userId.Value, ct);
        return ToActionResult(result);
    }

    /// <summary>
    /// Removes one chunk from the index.
    ///
    /// The source it came from is untouched — the document is still in the workspace, and
    /// re-uploading it indexes it again. This says what the assistant may draw on, and is not
    /// a retention or deletion request against the document itself.
    /// </summary>
    [Authorize]
    [HttpDelete("{chunkId}")]
    public async Task<IActionResult> DeleteChunk(Guid workspaceId, string chunkId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _knowledgeService.DeleteKnowledgeChunkAsync(
            workspaceId, chunkId, userId.Value, ct);

        if (result.IsSuccess) return NoContent();
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}

// The System Admin listing that used to live here is gone on purpose: what a workspace has
// indexed — documents, meeting summaries, glossary facts — is tenant content, and the decision
// of 2026-08-17 is that the admin portal sees a workspace's operational facts (membership,
// billing, lifecycle) and none of its content. The service method went with it.
