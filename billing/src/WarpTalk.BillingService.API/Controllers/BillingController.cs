using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Billing service API controller for managing plans, subscriptions, and tokens.
/// Follows clean architecture - minimal dependencies, delegates to service layer.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class BillingController : ControllerBase
{
    private readonly IBillingService _billingService;
    private readonly IWorkspaceValidationService _workspaceValidationService;

    public BillingController(IBillingService billingService, IWorkspaceValidationService workspaceValidationService)
    {
        _billingService = billingService;
        _workspaceValidationService = workspaceValidationService;
    }

    /// <summary>Get all available billing plans (public)</summary>
    [HttpGet("plans")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<PlanDto>), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 500)]
    public async Task<IActionResult> GetPlans(CancellationToken ct)
    {
        var result = await _billingService.GetPlansAsync(ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
    }

    /// <summary>Get current workspace token balance</summary>
    [HttpGet("workspaces/{workspaceId:guid}/tokens")]
    [ProducesResponseType(typeof(WorkspaceTokensDto), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    public async Task<IActionResult> GetWorkspaceTokens(Guid workspaceId, CancellationToken ct)
    {
        await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
        
        var result = await _billingService.GetWorkspaceTokensAsync(workspaceId, ct);
        if (!result.IsSuccess)
            return result.ErrorCode == ErrorCodes.BillingSubscriptionNotFound
                ? NotFound(new ApiErrorResponse(result.Error, result.ErrorCode))
                : BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        
        return Ok(result.Value);
    }

    /// <summary>Get active subscription for workspace</summary>
    [HttpGet("workspaces/{workspaceId:guid}/subscriptions/active")]
    [ProducesResponseType(typeof(SubscriptionDto), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    public async Task<IActionResult> GetActiveSubscription(Guid workspaceId, CancellationToken ct)
    {
        await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
        
        var result = await _billingService.GetActiveSubscriptionAsync(workspaceId, ct);
        if (!result.IsSuccess)
            return result.ErrorCode == ErrorCodes.BillingSubscriptionNotFound
                ? NotFound(new ApiErrorResponse(result.Error, result.ErrorCode))
                : BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        
        return Ok(result.Value);
    }

    /// <summary>Create a new subscription for workspace with plan validation</summary>
    [HttpPost("workspaces/{workspaceId:guid}/subscriptions")]
    [ProducesResponseType(typeof(SubscriptionDto), 201)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    [ProducesResponseType(typeof(ApiErrorResponse), 409)]
    public async Task<IActionResult> CreateSubscription(
        Guid workspaceId,
        [FromBody] CreateSubscriptionRequest request,
        CancellationToken ct)
    {
        await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
        
        var result = await _billingService.CreateSubscriptionAsync(
            workspaceId,
            request.PlanId,
            request.Duration ?? "1mo",
            request.Tier ?? "Premium",
            ct);

        if (!result.IsSuccess)
        {
            var statusCode = result.ErrorCode switch
            {
                ErrorCodes.BillingSubscriptionAlreadyActive => 409,
                ErrorCodes.BillingPlanNotFound => 404,
                _ => 400
            };
            return StatusCode(statusCode, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return CreatedAtAction(nameof(GetActiveSubscription), new { workspaceId }, result.Value);
    }

    /// <summary>Top-up tokens for workspace</summary>
    [HttpPost("workspaces/{workspaceId:guid}/tokens/topup")]
    [ProducesResponseType(typeof(int), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    public async Task<IActionResult> TopUpTokens(
        Guid workspaceId,
        [FromBody] TopUpTokensRequest request,
        CancellationToken ct)
    {
        await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
        
        var result = await _billingService.TopUpTokensAsync(workspaceId, request.Amount, null, null, ct);
        
        if (!result.IsSuccess)
            return result.ErrorCode == ErrorCodes.BillingSubscriptionNotFound
                ? NotFound(new ApiErrorResponse(result.Error, result.ErrorCode))
                : BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        
        return Ok(result.Value);
    }

    /// <summary>Consume tokens for service usage</summary>
    [HttpPost("workspaces/{workspaceId:guid}/tokens/consume")]
    [ProducesResponseType(typeof(int), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 402)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    public async Task<IActionResult> ConsumeTokens(
        Guid workspaceId,
        [FromBody] ConsumeTokensRequest request,
        CancellationToken ct)
    {
        var result = await _billingService.ConsumeTokensAsync(
            workspaceId,
            request.Amount,
            request.ReferenceType,
            request.ReferenceId,
            ct);

        if (!result.IsSuccess)
        {
            var statusCode = result.ErrorCode switch
            {
                ErrorCodes.BillingInsufficientCredits => 402,
                ErrorCodes.BillingSubscriptionNotFound => 404,
                _ => 400
            };
            return StatusCode(statusCode, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    /// <summary>Get token transaction history with pagination</summary>
    [HttpGet("workspaces/{workspaceId:guid}/tokens/history")]
    [ProducesResponseType(typeof(PaginatedResponse<TokenTransactionDto>), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    public async Task<IActionResult> GetTokenHistory(
        Guid workspaceId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
        
        var result = await _billingService.GetTokenHistoryAsync(workspaceId, pageNumber, pageSize, ct);
        
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        
        return Ok(result.Value);
    }

    /// <summary>Get payment transaction history with pagination</summary>
    [HttpGet("workspaces/{workspaceId:guid}/transactions")]
    [ProducesResponseType(typeof(PaginatedResponse<TransactionDto>), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    public async Task<IActionResult> GetTransactionHistory(
        Guid workspaceId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
        
        var result = await _billingService.GetTransactionHistoryAsync(workspaceId, pageNumber, pageSize, ct);
        
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        
        return Ok(result.Value);
    }

    /// <summary>Cancel workspace subscription - returns only status code</summary>
    [HttpPost("workspaces/{workspaceId:guid}/subscriptions/cancel")]
    [ProducesResponseType(typeof(string), 200)]
    [ProducesResponseType(typeof(ApiErrorResponse), 400)]
    [ProducesResponseType(typeof(ApiErrorResponse), 403)]
    [ProducesResponseType(typeof(ApiErrorResponse), 404)]
    public async Task<IActionResult> CancelSubscription(
        Guid workspaceId,
        [FromBody] CancelSubscriptionRequest request,
        CancellationToken ct)
    {
        await _workspaceValidationService.ValidateAsync(workspaceId, User, ct);
        
        var result = await _billingService.CancelSubscriptionAsync(workspaceId, request.CancellationReason, ct);
        
        if (!result.IsSuccess)
            return result.ErrorCode == ErrorCodes.BillingSubscriptionNotFound
                ? NotFound(new ApiErrorResponse(result.Error, result.ErrorCode))
                : BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        
        return Ok(result.Value);
    }
}

// DTO Request helpers
public record CancelSubscriptionRequest(string? CancellationReason = null);
