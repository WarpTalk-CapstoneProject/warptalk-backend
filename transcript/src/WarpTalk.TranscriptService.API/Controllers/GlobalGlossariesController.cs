using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;

namespace WarpTalk.TranscriptService.API.Controllers;

/// <summary>
/// Platform-admin CRUD + publish/archive lifecycle for the system-managed global glossary.
/// Route follows the ~/api/v1/admin/notifications precedent (NotificationsController) —
/// [Authorize(Roles = "admin")] checks the "admin" system role seeded in init-db.sql and put
/// into the JWT's ClaimTypes.Role claims by JwtTokenGenerator. See
/// docs/global-glossary-plan.md §3/§5.2.
/// </summary>
[ApiController]
[Route("api/v1/admin/global-glossary")]
[Authorize(Roles = "admin")]
public class GlobalGlossariesController : ControllerBase
{
    private readonly IGlobalGlossaryService _globalGlossaryService;

    public GlobalGlossariesController(IGlobalGlossaryService globalGlossaryService)
    {
        _globalGlossaryService = globalGlossaryService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResultDto<GlobalGlossaryTermDto>>> GetTerms(
        [FromQuery] GlobalGlossaryTermQuery query, CancellationToken cancellationToken)
    {
        var result = await _globalGlossaryService.GetTermsAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<GlobalGlossaryTermDto>> GetTerm(Guid id, CancellationToken cancellationToken)
    {
        var result = await _globalGlossaryService.GetTermByIdAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPost]
    public async Task<ActionResult<GlobalGlossaryTermDto>> CreateTerm(
        [FromBody] CreateGlobalGlossaryTermDto request, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId)) return Unauthorized();

        var result = await _globalGlossaryService.CreateTermAsync(request, actorId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return CreatedAtAction(nameof(GetTerm), new { id = result.Value!.Id }, result.Value);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateTerm(Guid id, [FromBody] UpdateGlobalGlossaryTermDto request, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId)) return Unauthorized();

        var result = await _globalGlossaryService.UpdateTermAsync(id, request, actorId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteTerm(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId)) return Unauthorized();

        var result = await _globalGlossaryService.DeleteTermAsync(id, actorId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return NoContent();
    }

    [HttpPost("{id}/publish")]
    public async Task<ActionResult> PublishTerm(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId)) return Unauthorized();

        var result = await _globalGlossaryService.PublishTermAsync(id, actorId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok();
    }

    [HttpPost("{id}/archive")]
    public async Task<ActionResult> ArchiveTerm(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId)) return Unauthorized();

        var result = await _globalGlossaryService.ArchiveTermAsync(id, actorId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok();
    }

    [HttpPost("bulk-import")]
    public async Task<ActionResult<BulkImportResultDto>> BulkImport(
        [FromBody] BulkImportGlobalGlossaryTermsDto request, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId)) return Unauthorized();

        var result = await _globalGlossaryService.BulkImportAsync(request, actorId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("{id}/audits")]
    public async Task<ActionResult> GetAudits(Guid id, CancellationToken cancellationToken)
    {
        var result = await _globalGlossaryService.GetAuditsAsync(id, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    private bool TryGetActorId(out Guid actorId)
    {
        var idString = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(idString, out actorId);
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
