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
    private readonly ICreditAndUsageService _creditService;
    private readonly IWebHostEnvironment _env;

    public CreditsController(ICreditAndUsageService creditService, IWebHostEnvironment env)
    {
        _creditService = creditService;
        _env = env;
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
    /// Requires [Authorize] in production; bypassed only in Development for sandbox testing.
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
    /// Record usage for a workspace.
    /// Requires [Authorize] in production; bypassed only in Development for sandbox testing.
    /// </summary>
    [HttpPost("workspace/{workspaceId:guid}/record-usage")]
    public async Task<ActionResult> RecordUsage(
        Guid workspaceId,
        [FromBody] RecordUsageRequest request,
        CancellationToken cancellationToken)
    {
        var actualRequest = new RecordUsageRequest(
            workspaceId,
            request.UserId,
            request.UsageType,
            request.Unit,
            request.Quantity,
            request.CreditsConsumed,
            request.DurationSeconds,
            request.TranslationRoomId,
            request.Details);

        var result = await _creditService.RecordUsageAsync(actualRequest, cancellationToken);
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
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetCreditHistoryAsync(workspaceId, pageNumber, pageSize, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Generate a billing report for a workspace for a specific month and year.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}/report")]
    public async Task<ActionResult<BillingReportDto>> GetBillingReport(
        [FromServices] ISubscriptionManagementService subscriptionService,
        Guid workspaceId,
        [FromQuery] int month,
        [FromQuery] int year,
        CancellationToken cancellationToken = default)
    {
        var result = await _creditService.GetBillingReportAsync(workspaceId, year, month, cancellationToken);
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
