using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;

namespace WarpTalk.TranscriptService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class GlossariesController : ControllerBase
{
    private readonly IGlossaryService _glossaryService;
    private readonly IGlobalGlossaryService _globalGlossaryService;
    private readonly IWorkspaceMembershipClient _membershipClient;

    public GlossariesController(
        IGlossaryService glossaryService,
        IGlobalGlossaryService globalGlossaryService,
        IWorkspaceMembershipClient membershipClient)
    {
        _glossaryService = glossaryService;
        _globalGlossaryService = globalGlossaryService;
        _membershipClient = membershipClient;
    }

    private bool TryGetUserId(out Guid userId)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdString, out userId);
    }

    /// <summary>
    /// Every glossary and term the caller can name is reachable by a workspace-scoped id the
    /// caller supplies — a glossary id, a workspace id — and IGlossaryService trusts whatever id
    /// it is given. Nothing before this fix asked whether the caller actually belongs to that
    /// workspace, so any authenticated user of the platform could read, create, edit and delete
    /// another workspace's glossary terms just by knowing or guessing its id. This is the one
    /// place that gap is closed: every action below resolves the workspace an id belongs to
    /// (fetching the glossary first when only a glossary id is in the route) and checks
    /// membership before doing anything with it.
    /// </summary>
    private async Task<ActionResult?> EnsureMemberAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var membership = await _membershipClient.GetMembershipAsync(workspaceId, userId, cancellationToken);
        if (!membership.IsActiveMember)
            return StatusCode(403, "Access denied. User is not an active member of this workspace.");

        return null;
    }

    /// <summary>Glossary CRUD is Owner/Admin only — a Member reads, per the product's own rule.</summary>
    private async Task<ActionResult?> EnsureManagerAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var membership = await _membershipClient.GetMembershipAsync(workspaceId, userId, cancellationToken);
        if (!membership.IsOwnerOrAdmin)
            return StatusCode(403, "Only the workspace Owner or Admin can manage the glossary.");

        return null;
    }

    /// <summary>
    /// Read-only, any authenticated user: the currently-published global glossary terms, so the
    /// workspace Terminology UI can show which system-managed terms apply and let the user
    /// override one with a workspace-level term of the same key. See
    /// docs/global-glossary-plan.md §5.5.4. Unlike GlobalGlossariesController this has no
    /// [Authorize(Roles = "admin")] — it only ever returns published rows.
    /// </summary>
    [HttpGet("global")]
    public async Task<ActionResult<IEnumerable<GlobalGlossaryTermDto>>> GetPublishedGlobalTerms(CancellationToken cancellationToken)
    {
        var result = await _globalGlossaryService.GetTermsAsync(
            new GlobalGlossaryTermQuery(Page: 1, PageSize: 200, Status: "published"), cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value!.Items);
    }

    [HttpPost]
    public async Task<ActionResult> CreateGlossary([FromBody] CreateGlossaryDto request, CancellationToken cancellationToken)
    {
        var authError = await EnsureManagerAsync(request.WorkspaceId, cancellationToken);
        if (authError != null) return authError;

        var result = await _glossaryService.CreateGlossaryAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        // WT-558: 201 WITH the created glossary. The empty 201 it used to return left a client
        // that had just made a glossary unable to name it, so adding terms in the same breath
        // meant re-listing and guessing which one was new.
        return StatusCode(201, result.Value);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<GlossaryDto>> GetGlossary(Guid id, CancellationToken cancellationToken)
    {
        var result = await _glossaryService.GetGlossaryByIdAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        var authError = await EnsureMemberAsync(result.Value!.WorkspaceId, cancellationToken);
        if (authError != null) return authError;

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}")]
    public async Task<ActionResult<IEnumerable<GlossaryDto>>> GetGlossariesByWorkspace(Guid workspaceId, CancellationToken cancellationToken)
    {
        var authError = await EnsureMemberAsync(workspaceId, cancellationToken);
        if (authError != null) return authError;

        var result = await _glossaryService.GetGlossariesByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateGlossary(Guid id, [FromBody] UpdateGlossaryDto request, CancellationToken cancellationToken)
    {
        var existing = await _glossaryService.GetGlossaryByIdAsync(id, cancellationToken);
        if (!existing.IsSuccess) return HandleFailure(existing.ErrorCode, existing.Error);

        var authError = await EnsureManagerAsync(existing.Value!.WorkspaceId, cancellationToken);
        if (authError != null) return authError;

        var result = await _glossaryService.UpdateGlossaryAsync(id, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteGlossary(Guid id, CancellationToken cancellationToken)
    {
        var existing = await _glossaryService.GetGlossaryByIdAsync(id, cancellationToken);
        if (!existing.IsSuccess) return HandleFailure(existing.ErrorCode, existing.Error);

        var authError = await EnsureManagerAsync(existing.Value!.WorkspaceId, cancellationToken);
        if (authError != null) return authError;

        var result = await _glossaryService.DeleteGlossaryAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return NoContent();
    }

    // --- Terms ---

    [HttpPost("{id}/terms")]
    public async Task<ActionResult> AddTerm(Guid id, [FromBody] CreateGlossaryTermDto request, CancellationToken cancellationToken)
    {
        var glossary = await _glossaryService.GetGlossaryByIdAsync(id, cancellationToken);
        if (!glossary.IsSuccess) return HandleFailure(glossary.ErrorCode, glossary.Error);

        var authError = await EnsureManagerAsync(glossary.Value!.WorkspaceId, cancellationToken);
        if (authError != null) return authError;

        var result = await _glossaryService.AddTermAsync(id, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return StatusCode(201);
    }

    /// <summary>
    /// WT-472: import a whole spreadsheet in one request.
    ///
    /// Answers 200 with the counts rather than 201, because the interesting part of the response is
    /// how many rows landed and how many were skipped — a bare 201 would tell the caller nothing
    /// about a file where half the rows were already present.
    /// </summary>
    [HttpPost("{id}/terms/bulk")]
    public async Task<ActionResult<BulkImportGlossaryTermsResultDto>> BulkImportTerms(
        Guid id,
        [FromBody] BulkImportGlossaryTermsDto request,
        CancellationToken cancellationToken)
    {
        var glossary = await _glossaryService.GetGlossaryByIdAsync(id, cancellationToken);
        if (!glossary.IsSuccess) return HandleFailure(glossary.ErrorCode, glossary.Error);

        var authError = await EnsureManagerAsync(glossary.Value!.WorkspaceId, cancellationToken);
        if (authError != null) return authError;

        var result = await _glossaryService.BulkImportTermsAsync(id, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("{id}/terms")]
    public async Task<ActionResult<IEnumerable<GlossaryTermDto>>> GetTerms(Guid id, CancellationToken cancellationToken)
    {
        var glossary = await _glossaryService.GetGlossaryByIdAsync(id, cancellationToken);
        if (!glossary.IsSuccess) return HandleFailure(glossary.ErrorCode, glossary.Error);

        var authError = await EnsureMemberAsync(glossary.Value!.WorkspaceId, cancellationToken);
        if (authError != null) return authError;

        var result = await _glossaryService.GetTermsByGlossaryIdAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPut("{id}/terms/{termId}")]
    public async Task<ActionResult> UpdateTerm(Guid id, Guid termId, [FromBody] UpdateGlossaryTermDto request, CancellationToken cancellationToken)
    {
        var glossary = await _glossaryService.GetGlossaryByIdAsync(id, cancellationToken);
        if (!glossary.IsSuccess) return HandleFailure(glossary.ErrorCode, glossary.Error);

        var authError = await EnsureManagerAsync(glossary.Value!.WorkspaceId, cancellationToken);
        if (authError != null) return authError;

        var result = await _glossaryService.UpdateTermAsync(id, termId, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok();
    }

    [HttpDelete("{id}/terms/{termId}")]
    public async Task<ActionResult> DeleteTerm(Guid id, Guid termId, CancellationToken cancellationToken)
    {
        var glossary = await _glossaryService.GetGlossaryByIdAsync(id, cancellationToken);
        if (!glossary.IsSuccess) return HandleFailure(glossary.ErrorCode, glossary.Error);

        var authError = await EnsureManagerAsync(glossary.Value!.WorkspaceId, cancellationToken);
        if (authError != null) return authError;

        var result = await _glossaryService.DeleteTermAsync(id, termId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return NoContent();
    }

    private ActionResult HandleFailure(string? errorCode, string? error)
    {
        return errorCode switch
        {
            "NOT_FOUND" => NotFound(error),
            "BAD_REQUEST" => BadRequest(error),
            // WT-601: a duplicate term is the caller's to fix, and 500 told them it was ours.
            "CONFLICT" => Conflict(error),
            "UNAUTHORIZED" => StatusCode(403, error),
            _ => StatusCode(500, error)
        };
    }
}
