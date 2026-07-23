using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.BillingService.API.Filters;
using WarpTalk.BillingService.Domain.Interfaces;


namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly IPaymentAppService _paymentAppService;
    private readonly IStripeWebhookService _stripeWebhookService;
    private readonly IWorkspaceClient _workspaceClient;

    public PaymentsController(
        IPaymentService paymentService,
        IPaymentAppService paymentAppService,
        IStripeWebhookService stripeWebhookService,
        IWorkspaceClient workspaceClient)
    {
        _paymentService = paymentService;
        _paymentAppService = paymentAppService;
        _stripeWebhookService = stripeWebhookService;
        _workspaceClient = workspaceClient;
    }

    [HttpGet("workspace/{workspaceId}/history")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<PaginatedResponse<PaymentTransactionDto>>> GetPaymentHistory(
        Guid workspaceId,
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _paymentService.GetPaymentHistoryAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpPost]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<PaymentTransactionDto>> CreatePayment([FromBody] CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var result = await _paymentService.CreatePaymentAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return StatusCode(201, result.Value);
    }

    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleWebhook([FromBody] PaymentWebhookRequest request, CancellationToken cancellationToken)
    {
        var result = await _paymentService.HandleWebhookAsync(request, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(new { message = "Webhook processed successfully." });
    }

    [HttpPost("checkout")]
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateCheckoutSessionRequest request)
    {
        try
        {
            string url = await _paymentAppService.CreateCheckoutSessionAsync(request);
            return Ok(new { url });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiErrorResponse(ex.Message, ErrorCodes.ValidationError));
        }
    }

    [HttpGet("checkout-session/{sessionId}")]
    [Authorize]
    public async Task<IActionResult> GetCheckoutSession(string sessionId)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        try
        {
            var session = await _paymentAppService.GetCheckoutSessionAsync(sessionId);

            // Validate workspace ID from session metadata
            string workspaceIdStr = session.Metadata.GetValueOrDefault(BillingConstants.StripeMetadata.WorkspaceId, string.Empty);
            if (!Guid.TryParse(workspaceIdStr, out Guid workspaceId))
            {
                return BadRequest(new ApiErrorResponse(ApiMessageConstants.ErrorMessages.BillingWorkspaceIdNotInSessionMetadata, ErrorCodes.ValidationError));
            }

            // Verify the requesting user is an Owner or Admin of this workspace
            bool hasAccess = await _workspaceClient.VerifyWorkspaceRolesAsync(workspaceId, userId.Value, "Owner", "Admin");
            if (!hasAccess)
            {
                return StatusCode(403, new ApiErrorResponse(ApiMessageConstants.ErrorMessages.BillingAccessDeniedOwnerAdminRequired, ErrorCodes.Forbidden));
            }

            // Fallback: process payment event inline if session already marked as paid
            if (session.PaymentStatus == BillingConstants.Payments.StatusPaid)
            {
                bool isZeroDecimal = string.Equals(session.Currency, BillingConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase);
                decimal finalAmount = isZeroDecimal ? (session.AmountTotal ?? 0) : ((session.AmountTotal ?? 0) / 100m);

                await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                    StripeSessionId: session.Id,
                    PaymentIntentId: !string.IsNullOrEmpty(session.PaymentIntentId) ? session.PaymentIntentId : string.Empty,
                    Amount: finalAmount,
                    Currency: session.Currency,
                    UserIdStr: session.Metadata.GetValueOrDefault(BillingConstants.StripeMetadata.UserId, string.Empty),
                    WorkspaceIdStr: session.Metadata.GetValueOrDefault(BillingConstants.StripeMetadata.WorkspaceId, string.Empty),
                    PaymentType: session.Metadata.GetValueOrDefault(BillingConstants.StripeMetadata.PaymentType, string.Empty),
                    Status: BillingConstants.Payments.StatusPaid,
                    PlanSlug: session.Metadata.GetValueOrDefault(BillingConstants.StripeMetadata.PlanSlug, string.Empty),
                    BillingCycle: session.Metadata.GetValueOrDefault(BillingConstants.StripeMetadata.BillingCycle, string.Empty)
                ));
            }

            return Ok(session);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiErrorResponse(ex.Message, ErrorCodes.ValidationError));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, ErrorCodes.NotFound));
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiErrorResponse(ex.Message, ErrorCodes.ValidationError));
        }
    }

    [HttpPost("webhook/stripe")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var stripeSignature = Request.Headers["Stripe-Signature"].ToString();

        try
        {
            bool result = await _stripeWebhookService.HandleWebhookAsync(json, stripeSignature);
            if (!result)
            {
                return BadRequest(new ApiErrorResponse(ApiMessageConstants.ErrorMessages.BillingStripeWebhookFailed, ErrorCodes.InternalServerError));
            }

            return Ok();
        }
        catch (Stripe.StripeException ex)
        {
            return BadRequest(new ApiErrorResponse(ex.Message, ErrorCodes.ValidationError));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new ApiErrorResponse(ex.Message, ErrorCodes.InternalServerError));
        }
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        errorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new ApiErrorResponse(error ?? ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound, errorCode)),
            ErrorCodes.BillingPlanNotFound => BadRequest(new ApiErrorResponse(error ?? ApiMessageConstants.ErrorMessages.BillingPlanNotFound, errorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(error ?? ApiMessageConstants.ErrorMessages.BillingAccessDenied, errorCode)),
            _ => StatusCode(500, new ApiErrorResponse(error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, errorCode ?? ErrorCodes.InternalServerError))
        };
}
