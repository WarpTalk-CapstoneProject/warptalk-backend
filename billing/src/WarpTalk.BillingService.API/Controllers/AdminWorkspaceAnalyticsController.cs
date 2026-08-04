using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Per-workspace analytics and credit ledger for the System Admin portal (WT-206).
///
/// Routed under ~/api/v1/admin/billing rather than ~/api/v1/admin/workspaces because the
/// gateway already forwards the latter to the workspace service; keeping the prefixes distinct
/// avoids a routing rule whose behaviour depends on match ordering.
///
/// Read-only: nothing here writes to the ledger. Gated by the shared system-admin policy.
/// </summary>
[ApiController]
[Route("api/v1/admin/billing/workspaces/{workspaceId:guid}")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminWorkspaceAnalyticsController : ControllerBase
{
    private readonly IAdminWorkspaceAnalyticsService _analyticsService;

    public AdminWorkspaceAnalyticsController(IAdminWorkspaceAnalyticsService analyticsService)
    {
        _analyticsService = analyticsService;
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> GetAnalytics(
        Guid workspaceId,
        [FromQuery] AdminDateRange range,
        CancellationToken ct)
    {
        var result = await _analyticsService.GetAnalyticsAsync(workspaceId, range, ct);
        return ToActionResult(result);
    }

    [HttpGet("credit-transactions")]
    public async Task<IActionResult> GetCreditTransactions(
        Guid workspaceId,
        [FromQuery] AdminCreditTransactionQuery query,
        CancellationToken ct)
    {
        var result = await _analyticsService.GetCreditTransactionsAsync(workspaceId, query, ct);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
