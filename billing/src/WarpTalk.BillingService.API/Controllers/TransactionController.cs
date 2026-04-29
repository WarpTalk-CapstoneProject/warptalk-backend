using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
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
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty)
        {
            return BadRequest(new { message = "X-Workspace-Id header is required." });
        }

        var transactions = await _paymentService.GetTransactionsByWorkspaceAsync(workspaceId, cancellationToken);
        return Ok(transactions);
    }

    /// <summary>
    /// Lấy nhật ký biến động Quota (Usage Logs)
    /// </summary>
    [HttpGet("usage-logs")]
    public async Task<IActionResult> GetUsageLogs(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId, 
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty)
        {
            return BadRequest(new { message = "X-Workspace-Id header is required." });
        }

        var logs = await _paymentService.GetUsageLogsByWorkspaceAsync(workspaceId, cancellationToken);
        return Ok(logs);
    }
}

