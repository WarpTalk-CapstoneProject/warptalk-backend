using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.BillingService.API.Filters;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/credits")]
public class CreditsController : ControllerBase
{
    private readonly ICreditService _creditService;

    public CreditsController(ICreditService creditService)
    {
        _creditService = creditService;
    }

    /// <summary>
    /// Get the current credit balance for a workspace.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<CreditBalanceDto>> GetWorkspaceCredits(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Paginated credit transaction history for a workspace.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}/history")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<PagedResult<CreditTransactionDto>>> GetCreditHistory(
        Guid workspaceId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? type = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int? minAmount = null,
        [FromQuery] int? maxAmount = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetCreditHistoryAsync(
            workspaceId, pageNumber, pageSize, cancellationToken, type, fromDate, toDate, minAmount, maxAmount);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Manually adjust credits for a workspace (Admin only).
    /// </summary>
    [HttpPost("workspace/{workspaceId:guid}/adjust")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CreditTransactionDto>> AdjustCredits(
        Guid workspaceId,
        [FromBody] AdjustCreditsRequest request,
        [FromServices] IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var sub = await unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
            cancellationToken);

        if (sub is null)
            return NotFound(new { message = "No active subscription found for workspace." });

        var adminUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(adminUserId, out var administratorId))
            return Unauthorized(new { message = "Invalid or missing administrator identity." });

        var result = await _creditService.AdjustCreditsAsync(
            sub.Id, request.Amount, request.Reason, administratorId, cancellationToken);

        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    [HttpGet("history/global")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PagedResult<CreditTransactionDto>>> GetGlobalCreditHistory(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? workspaceId = null,
        [FromQuery] string? type = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int? minAmount = null,
        [FromQuery] int? maxAmount = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetGlobalCreditHistoryAsync(pageNumber, pageSize, cancellationToken, workspaceId, type, fromDate, toDate, minAmount, maxAmount);
        if (!result.IsSuccess) return HandleFailure(result);
        return Ok(result.Value);
    }

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new { message = result.Error }),
            ErrorCodes.BillingInsufficientCredits => UnprocessableEntity(new { message = result.Error }),
            "FEATURE_NOT_AVAILABLE" => StatusCode(403, new { message = result.Error }),
            "INVALID_REQUEST" => BadRequest(new { message = result.Error }),
            _ => StatusCode(500, new { message = result.Error })
        };
}
