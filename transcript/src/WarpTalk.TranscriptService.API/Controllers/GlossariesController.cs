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

    public GlossariesController(IGlossaryService glossaryService)
    {
        _glossaryService = glossaryService;
    }

    [HttpPost]
    public async Task<ActionResult> CreateGlossary([FromBody] CreateGlossaryDto request, CancellationToken cancellationToken)
    {
        var result = await _glossaryService.CreateGlossaryAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);
        
        return StatusCode(201);
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
