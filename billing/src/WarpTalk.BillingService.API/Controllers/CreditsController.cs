using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class CreditsController : ControllerBase
{
    private readonly ICreditService _creditService;

    public CreditsController(ICreditService creditService)
    {
        _creditService = creditService;
    }

    [HttpGet("workspace/{workspaceId}")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<CreditBalanceDto>> GetWorkspaceCredits(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("workspace/{workspaceId}/history")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetCreditHistory(Guid workspaceId, [FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetCreditHistoryAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpGet("history/global")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistory([FromQuery] CreditHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetGlobalCreditHistoryAsync(query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// The status a failure deserves, rather than 400 for all of them.
    ///
    /// A workspace with no subscription answered 400 Bad Request. Nothing about the request was
    /// bad — the URL, the id and the caller's role were all fine; the workspace simply has no
    /// plan. Production showed four of these per page load on the dashboard, and a client cannot
    /// tell that apart from a malformed request, so the only honest thing it could render was an
    /// error where there is no error.
    ///
    /// Same shape as AdminWorkspaceAnalyticsController.ToActionResult, which already made this
    /// distinction in this service.
    /// </summary>
    private ActionResult<T> ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        var error = new ApiErrorResponse(
            result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
            result.ErrorCode);

        return result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(error),
            ErrorCodes.NotFound => NotFound(error),
            ErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, error),
            ErrorCodes.InternalServerError => StatusCode(StatusCodes.Status500InternalServerError, error),
            _ => BadRequest(error),
        };
    }
}
