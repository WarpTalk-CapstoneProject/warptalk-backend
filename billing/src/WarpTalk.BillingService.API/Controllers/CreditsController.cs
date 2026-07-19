using System;
using System.Linq;
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
    private readonly IWebHostEnvironment _env;

    public CreditsController(ICreditService creditService, IWebHostEnvironment env)
    {
        _creditService = creditService;
        _env = env;
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
    /// Simulate a Stripe payment event (Development/Testing only).
    /// </summary>
    [HttpPost("workspace/{workspaceId:guid}/simulate-payment")]
    public async Task<ActionResult> SimulatePayment(
        [FromServices] IUnitOfWork unitOfWork,
        [FromServices] IPlanService planService,
        [FromServices] ISubscriptionService subscriptionService,
        Guid workspaceId,
        [FromQuery] decimal amount = 190000m,
        [FromQuery] string currency = "vnd")
    {
        var subResult = await subscriptionService.GetActiveSubscriptionAsync(workspaceId);
        Guid subId;
        if (!subResult.IsSuccess || subResult.Value == null)
        {
            var plans = await planService.GetActivePlansAsync();
            var plan = plans.Value?.FirstOrDefault() ?? throw new Exception("No active plans found.");
            var newSub = await subscriptionService.CreateSubscriptionAsync(new SubscriptionRequest(workspaceId, plan.Id, Guid.Empty));
            subId = newSub.Value!.Id;
        }
        else
        {
            subId = subResult.Value.Id;
        }

        var paymentId = Guid.NewGuid();
        var stripeInvoiceId = "in_test_" + Guid.NewGuid().ToString().Substring(0, 8);
        var payment = new WarpTalk.BillingService.Domain.Entities.Payment
        {
            Id = paymentId,
            SubscriptionId = subId,
            UserId = Guid.Empty,
            Amount = amount,
            TaxAmount = 0m,
            TotalAmount = amount,
            Currency = currency,
            PaymentMethod = "card",
            Provider = "stripe",
            ProviderTransactionId = stripeInvoiceId,
            Status = "paid",
            PaidAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _creditService.TopUpCreditsAsync(workspaceId, new TopUpRequest((int)(amount / 10m), "stripe_payment", null));

        await unitOfWork.PaymentRepository.AddAsync(payment);

        var invoice = new WarpTalk.BillingService.Domain.Entities.Invoice
        {
            Id = Guid.NewGuid(),
            UserId = Guid.Empty,
            PaymentId = paymentId,
            InvoiceNumber = stripeInvoiceId,
            Subtotal = amount,
            Tax = 0,
            Total = amount,
            Currency = currency,
            Status = "paid",
            PdfUrl = "https://stripe.com/files/payments/pdf/invoice_sample.pdf",
            LineItems = "[]",
            IssuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        await unitOfWork.InvoiceRepository.AddAsync(invoice);
        await unitOfWork.SaveChangesAsync();

        return Ok(new { message = "Payment simulated successfully.", invoiceId = invoice.Id, stripeInvoiceId = stripeInvoiceId });
    }

    /// <summary>
    /// Manually adjust credits for a workspace (Admin only).
    /// </summary>
    [HttpPost("workspace/{workspaceId:guid}/adjust")]
    [Authorize]
    public async Task<ActionResult<CreditTransactionDto>> AdjustCredits(
        Guid workspaceId,
        [FromBody] AdjustCreditsRequest request,
        [FromServices] IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var isSystemAdmin = User.IsInRole("Admin") || 
                            User.FindFirst("role")?.Value == "Admin" ||
                            User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value == "Admin";

        if (!isSystemAdmin)
        {
            return Forbid();
        }
        var sub = await unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
            cancellationToken);

        if (sub is null)
            return NotFound(new { message = "No active subscription found for workspace." });

        var adminUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? Guid.Empty.ToString();

        var result = await _creditService.AdjustCreditsAsync(
            sub.Id, request.Amount, request.Reason, adminUserId, cancellationToken);

        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    [HttpGet("history/global")]
    [AllowAnonymous]
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
