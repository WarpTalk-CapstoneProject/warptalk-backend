using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Security;
using WarpTalk.BillingService.Application.Services;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/transaction")]
public class TransactionController : ControllerBase
{
    private readonly IPaymentService _paymentService;

    public TransactionController(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <summary>
    /// Lấy lịch sử giao dịch (History)
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetTransactionHistory(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null)
        {
            return accessError;
        }

        if (page < 1 || pageSize < 1)
        {
            return BadRequest(new { message = "page and pageSize must be greater than 0." });
        }

        pageSize = Math.Min(pageSize, 200);

        var transactions = await _paymentService.GetTransactionsByWorkspaceAsync(workspaceId, page, pageSize, cancellationToken);
        return Ok(transactions);
    }

    /// <summary>
    /// Lấy nhật ký biến động Quota (Usage Logs)
    /// </summary>
    [HttpGet("usage-logs")]
    public async Task<IActionResult> GetUsageLogs(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null)
        {
            return accessError;
        }

        if (page < 1 || pageSize < 1)
        {
            return BadRequest(new { message = "page and pageSize must be greater than 0." });
        }

        pageSize = Math.Min(pageSize, 200);

        var logs = await _paymentService.GetUsageLogsByWorkspaceAsync(workspaceId, page, pageSize, cancellationToken);
        return Ok(logs);
    }
}

