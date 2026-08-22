using System;
using System.Collections.Generic;
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

    public GlossariesController(IGlossaryService glossaryService, IGlobalGlossaryService globalGlossaryService)
    {
        _glossaryService = glossaryService;
        _globalGlossaryService = globalGlossaryService;
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

        return Ok(result.Value);
    }

    [HttpGet("workspace/{workspaceId}")]
    public async Task<ActionResult<IEnumerable<GlossaryDto>>> GetGlossariesByWorkspace(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _glossaryService.GetGlossariesByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateGlossary(Guid id, [FromBody] UpdateGlossaryDto request, CancellationToken cancellationToken)
    {
        var result = await _glossaryService.UpdateGlossaryAsync(id, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteGlossary(Guid id, CancellationToken cancellationToken)
    {
        var result = await _glossaryService.DeleteGlossaryAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return NoContent();
    }

    // --- Terms ---

    [HttpPost("{id}/terms")]
    public async Task<ActionResult> AddTerm(Guid id, [FromBody] CreateGlossaryTermDto request, CancellationToken cancellationToken)
    {
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
        var result = await _glossaryService.BulkImportTermsAsync(id, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("{id}/terms")]
    public async Task<ActionResult<IEnumerable<GlossaryTermDto>>> GetTerms(Guid id, CancellationToken cancellationToken)
    {
        var result = await _glossaryService.GetTermsByGlossaryIdAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPut("{id}/terms/{termId}")]
    public async Task<ActionResult> UpdateTerm(Guid id, Guid termId, [FromBody] UpdateGlossaryTermDto request, CancellationToken cancellationToken)
    {
        var result = await _glossaryService.UpdateTermAsync(id, termId, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok();
    }

    [HttpDelete("{id}/terms/{termId}")]
    public async Task<ActionResult> DeleteTerm(Guid id, Guid termId, CancellationToken cancellationToken)
    {
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
            "UNAUTHORIZED" => StatusCode(403, error),
            _ => StatusCode(500, error)
        };
    }
}
