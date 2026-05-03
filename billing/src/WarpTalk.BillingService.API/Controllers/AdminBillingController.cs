using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.API.Controllers;

[Route("api/admin/billing")]
[ApiController]
[Authorize(Roles = "admin,owner")]
public class AdminBillingController : ControllerBase
{
    private readonly ITransactionService _transactionService;
    private readonly ISubscriptionService _subscriptionService;

    public AdminBillingController(
        ITransactionService transactionService,
        ISubscriptionService subscriptionService)
    {
        _transactionService = transactionService;
        _subscriptionService = subscriptionService;
    }

    // ===================================================
    // GET TRANSACTIONS BY WORKSPACE
    // ===================================================
    [HttpGet("transactions/{workspaceId}")]
    public async Task<IActionResult> GetWorkspaceTransactions(
        Guid workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        // Validation: workspaceId
        if (workspaceId == Guid.Empty)
            return BadRequest(new { message = "WorkspaceId is required and cannot be empty" });

        // Validation: pagination bounds
        const int MAX_PAGE_SIZE = 100;
        if (pageSize > MAX_PAGE_SIZE) pageSize = MAX_PAGE_SIZE;
        if (page < 1) page = 1;

        var skip = (page - 1) * pageSize;

        var result = await _transactionService.GetByWorkspaceAsync(
            workspaceId,
            skip,
            pageSize,
            ct);

        return Ok(result);
    }

    // ===================================================
    // GET SINGLE TRANSACTION
    // ===================================================
    [HttpGet("transactions/detail/{id}")]
    public async Task<IActionResult> GetTransactionById(
        Guid id,
        CancellationToken ct = default)
    {
        // Validation: id
        if (id == Guid.Empty)
            return BadRequest(new { message = "Transaction ID is required and cannot be empty" });

        var result = await _transactionService.GetByIdAsync(id, ct);

        if (result == null)
            return NotFound();

        return Ok(result);
    }

    // ===================================================
    // GET TRANSACTION BY ORDER CODE
    // ===================================================
    [HttpGet("transactions/order/{orderCode}")]
    public async Task<IActionResult> GetByOrderCode(
        long orderCode,
        CancellationToken ct = default)
    {
        var result = await _transactionService.GetByOrderCodeAsync(orderCode, ct);

        if (result == null)
            return NotFound();

        return Ok(result);
    }

    // ===================================================
    // CREATE SUBSCRIPTION
    // ===================================================
    [HttpPost("subscription/create")]
    public async Task<IActionResult> CreateSubscription(
        [FromBody] CreateSubscriptionCommand command,
        CancellationToken ct)
    {
        var result = await _subscriptionService.CreateAsync(command, ct);
        return Ok(result);
    }

    // ===================================================
    // UPGRADE SUBSCRIPTION
    // ===================================================
    [HttpPost("subscription/upgrade")]
    public async Task<IActionResult> UpgradeSubscription(
        [FromBody] UpgradeSubscriptionCommand command,
        CancellationToken ct)
    {
        await _subscriptionService.UpgradeAsync(command, ct);
        return Ok(new { message = "Subscription upgraded successfully" });
    }

    // ===================================================
    // CANCEL SUBSCRIPTION (FIXED SIGNATURE)
    // ===================================================
    [HttpPost("subscription/cancel")]
    public async Task<IActionResult> CancelSubscription(
        [FromQuery] Guid subscriptionId,
        CancellationToken ct)
    {
        await _subscriptionService.CancelAsync(subscriptionId, ct);
        return Ok(new { message = "Subscription cancelled" });
    }

    // ===================================================
    // GET ACTIVE SUBSCRIPTION
    // ===================================================
    [HttpGet("subscription/active/{workspaceId}")]
    public async Task<IActionResult> GetActiveSubscription(
        Guid workspaceId,
        CancellationToken ct)
    {
        // Validation: workspaceId
        if (workspaceId == Guid.Empty)
            return BadRequest(new { message = "WorkspaceId is required and cannot be empty" });

        var result = await _subscriptionService.GetActiveAsync(workspaceId, ct);
        return Ok(result);
    }
}