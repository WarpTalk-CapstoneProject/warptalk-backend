using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

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
    public async Task<ActionResult<CreditBalanceDto>> GetWorkspaceCredits(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var result = await _creditService.GetWorkspaceCreditsAsync(workspaceId, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Deduct credits from a workspace subscription.
    /// </summary>
    [HttpPost("workspace/{workspaceId:guid}/consume")]
    public async Task<ActionResult<CreditTransactionDto>> ConsumeCredits(
        Guid workspaceId,
        [FromBody] ConsumeCreditsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _creditService.ConsumeCreditsAsync(workspaceId, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Add credits to a workspace subscription (admin / payment webhook).
    /// </summary>
    [HttpPost("workspace/{workspaceId:guid}/topup")]
    public async Task<ActionResult<CreditBalanceDto>> TopUpCredits(
        Guid workspaceId,
        [FromBody] TopUpRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _creditService.TopUpCreditsAsync(workspaceId, request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Paginated credit transaction history for a workspace.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}/history")]
    public async Task<ActionResult<PagedResult<CreditTransactionDto>>> GetCreditHistory(
        Guid workspaceId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize   = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetCreditHistoryAsync(workspaceId, pageNumber, pageSize, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound   => NotFound(new { message = result.Error }),
            ErrorCodes.BillingInsufficientCredits    => UnprocessableEntity(new { message = result.Error }),
            "FEATURE_NOT_AVAILABLE"                  => StatusCode(403, new { message = result.Error }),
            _                                        => StatusCode(500, new { message = result.Error })
        };
}
