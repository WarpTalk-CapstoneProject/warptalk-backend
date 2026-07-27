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
/// 
/// Business rule: any domain that is not a public provider (gmail.com, yahoo.com, etc.)
/// is considered already verified — the enterprise is trusted to own its own domain.
/// Therefore no DNS challenge is required; domains are verified immediately upon addition.
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

    /// <summary>
    /// [Owner only] Adds a non-public corporate domain to the workspace.
    /// The domain is marked as verified immediately.
    /// </summary>
    [Authorize]
    [HttpPost]
    public async Task<IActionResult> AddDomain(
        Guid workspaceId,
        [FromBody] AddVerifiedDomainRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _verifiedDomainService.AddDomainAsync(workspaceId, request.Domain, userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    /// <summary>
    /// [Owner / Admin] Lists all active (non-revoked) verified domains for the workspace.
    /// </summary>
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> ListDomains(Guid workspaceId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _verifiedDomainService.ListDomainsAsync(workspaceId, userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    /// <summary>
    /// [Owner only] Revokes a verified domain.
    /// Blocked if it is the last active domain and the workspace still requires domain verification.
    /// </summary>
    [Authorize]
    [HttpDelete("{domainId:guid}")]
    public async Task<IActionResult> RevokeDomain(
        Guid workspaceId,
        Guid domainId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _verifiedDomainService.RevokeDomainAsync(workspaceId, domainId, userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }
}
