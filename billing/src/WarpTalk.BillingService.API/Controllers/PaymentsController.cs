using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.BillingService.API.Filters;
using WarpTalk.BillingService.API.GrpcServices;
using WarpTalk.BillingService.API.Services;
using WarpTalk.Shared.Extensions;
using Stripe;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/payments")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentAndLedgerService _paymentService;
    private readonly ICheckoutPricingService _checkoutPricingService;
    private readonly IStripeBillingGateway _stripe;
    private readonly BillingGrpcService _billingEvents;

    public PaymentsController(
        IPaymentAndLedgerService paymentService,
        ICheckoutPricingService checkoutPricingService,
        IStripeBillingGateway stripe,
        BillingGrpcService billingEvents)
    {
        _paymentService = paymentService;
        _checkoutPricingService = checkoutPricingService;
        _stripe = stripe;
        _billingEvents = billingEvents;
    }

    /// <summary>
    /// Paginated payment/transaction history for a workspace.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}/history")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<PagedResult<PaymentTransactionDto>>> GetPaymentHistory(
        Guid workspaceId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _paymentService.GetPaymentHistoryAsync(workspaceId, pageNumber, pageSize, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }




    /// <summary>
    /// Create a pending payment checkout for a subscription.
    /// </summary>
    [HttpPost]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<PaymentTransactionDto>> CreatePayment(
        [FromBody] CreatePaymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _paymentService.CreatePaymentAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return StatusCode(201, result.Value);
    }

    [HttpPost("checkout")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<IActionResult> CreateCheckoutSession(
        [FromBody] CreateCheckoutSessionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { message = "Invalid or missing user identity." });

        var resolved = await _checkoutPricingService.ResolveAsync(
            request,
            userId.Value,
            cancellationToken);

        if (!resolved.IsSuccess)
            return HandleFailure(resolved);

        var url = await _stripe.CreateCheckoutAsync(resolved.Value!, cancellationToken);
        return Ok(new { url });
    }

    [HttpGet("checkout-session/{sessionId}")]
    public async Task<IActionResult> GetCheckoutSession(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { message = "Invalid or missing user identity." });

        var session = await _stripe.GetCheckoutSessionAsync(sessionId, cancellationToken);
        if (session is null)
            return NotFound(new { message = "Checkout session was not found." });

        if (!session.Metadata.TryGetValue("UserId", out var sessionUserId)
            || !Guid.TryParse(sessionUserId, out var ownerId)
            || ownerId != userId.Value)
        {
            return Forbid();
        }

        return Ok(new
        {
            id = session.Id,
            amountTotal = session.AmountTotal,
            currency = session.Currency,
            metadata = session.Metadata,
            paymentStatus = session.PaymentStatus,
            status = session.Status,
            paymentIntentId = session.PaymentIntentId
        });
    }

    /// <summary>
    /// Receives signed Stripe events. Credit and subscription state are changed
    /// only after signature validation succeeds.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleWebhook(
        CancellationToken cancellationToken)
    {
        string payload;
        using (var reader = new StreamReader(Request.Body))
            payload = await reader.ReadToEndAsync(cancellationToken);

        try
        {
            var paymentEvent = _stripe.ParseWebhook(
                payload,
                Request.Headers["Stripe-Signature"].ToString());

            if (paymentEvent is null)
                return Ok();

            var result = await _billingEvents.ProcessPaymentEventCoreAsync(
                paymentEvent,
                cancellationToken);

            if (!result.Success)
                return StatusCode(500, new { message = result.ErrorMessage });

            return Ok();
        }
        catch (StripeException)
        {
            return BadRequest(new { message = "Invalid Stripe webhook signature or payload." });
        }
    }

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new { message = result.Error }),
            ErrorCodes.BillingPlanNotFound => BadRequest(new { message = result.Error }),
            ErrorCodes.Forbidden => StatusCode(403, new { message = result.Error }),
            ErrorCodes.ValidationError => BadRequest(new { message = result.Error }),
            "FEATURE_NOT_AVAILABLE" => StatusCode(403, new { message = result.Error }),
            _ => StatusCode(500, new { message = result.Error })
        };
}
