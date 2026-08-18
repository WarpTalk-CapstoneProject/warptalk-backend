using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.WorkspaceService.Application.DTOs.VerifiedDomain;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// Manages the verified corporate domains associated with a workspace.
/// </summary>
[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/verified-domains")]
public class WorkspaceVerifiedDomainsController : ControllerBase
{
    private readonly IVerifiedDomainService _verifiedDomainService;

    public WorkspaceVerifiedDomainsController(IVerifiedDomainService verifiedDomainService)
    {
        _verifiedDomainService = verifiedDomainService;
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> AddDomain(
        Guid workspaceId,
        [FromBody] AddVerifiedDomainRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _verifiedDomainService.AddDomainAsync(
            workspaceId, request.Domain, userId.Value, request.ConsentVersion, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> ListDomains(Guid workspaceId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _verifiedDomainService.ListDomainsAsync(workspaceId, userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [Authorize]
    [HttpDelete("{domainId:guid}")]
    public async Task<IActionResult> RevokeDomain(
        Guid workspaceId,
        Guid domainId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(new ApiErrorResponse("Unauthorized", ErrorCodes.Unauthorized));

        var result = await _verifiedDomainService.RevokeDomainAsync(workspaceId, domainId, userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }
}
