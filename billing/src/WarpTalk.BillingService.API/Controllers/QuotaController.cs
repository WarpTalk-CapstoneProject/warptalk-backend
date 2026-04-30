using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Security;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/quota")]
public class QuotaController : ControllerBase
{
    private readonly IQuotaService _quotaService;

    public QuotaController(IQuotaService quotaService)
    {
        _quotaService = quotaService;
    }

    /// <summary>
    /// Kiểm tra trạng thái Quota (Check)
    /// </summary>
    /// <param name="workspaceId">Lấy từ header X-Workspace-Id</param>
    [HttpGet("check")]
    public async Task<IActionResult> CheckQuota(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId, 
        CancellationToken cancellationToken)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null)
        {
            return accessError;
        }

        var response = await _quotaService.CheckQuotaAsync(workspaceId, cancellationToken);
        
        if (!response.HasQuota)
        {
            return StatusCode(403, response); // Forbidden if no quota
        }

        return Ok(response);
    }

    /// <summary>
    /// Trừ Quota (Deduct)
    /// </summary>
    [HttpPost("deduct")]
    public async Task<IActionResult> DeductQuota(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromBody] QuotaDeductRequest request, 
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null)
        {
            return accessError;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var response = await _quotaService.DeductQuotaAsync(workspaceId, request, cancellationToken);
        if (response.Success)
        {
            return Ok(response);
        }
        else
        {
            // Logic map status codes as per spec
            if (response.ErrorCode == "InsufficientQuota")
                return StatusCode(402, response); // Payment Required
            
            if (response.ErrorCode == "IdempotentRequestAlreadyProcessed")
                return Ok(response); // Return 200 for idempotency hits

            return BadRequest(response);
        }
    }

    /// <summary>
    /// Hoàn trả Quota (Refund)
    /// </summary>
    [HttpPost("refund")]
    public async Task<IActionResult> RefundQuota(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromBody] QuotaRefundRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null)
        {
            return accessError;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var response = await _quotaService.RefundQuotaAsync(workspaceId, request, cancellationToken);
        if (response.Success)
        {
            return Ok(response);
        }
        
        return BadRequest(response);
    }

    [AllowAnonymous]
    [HttpGet("plans")]
    public async Task<IActionResult> GetPlans(CancellationToken cancellationToken)
    {
        var plans = await _quotaService.GetAvailablePlansAsync(cancellationToken);
        return Ok(plans);
    }

    [HttpPost("upgrade")]
    public async Task<IActionResult> UpgradePlan(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId, 
        [FromBody] Guid planId, 
        CancellationToken cancellationToken)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null)
        {
            return accessError;
        }

        var success = await _quotaService.UpgradePlanAsync(workspaceId, planId, cancellationToken);
        if (success)
        {
            return Ok(new { message = "Plan upgraded successfully" });
        }
        return BadRequest(new { message = "Failed to upgrade plan." });
    }
}

