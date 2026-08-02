using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Extensions;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

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
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdminSystem)]
    public async Task<ActionResult<PaginatedResponse<PaymentTransactionDto>>> GetPaymentHistory(
        Guid workspaceId,
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _paymentService.GetPaymentHistoryAsync(workspaceId, query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPost]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdminSystem)]
    public async Task<ActionResult<PaymentTransactionDto>> CreatePayment([FromBody] CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var result = await _paymentService.CreatePaymentAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return StatusCode(201, result.Value);
    }

    [HttpPost("checkout")]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdminSystem)]
    public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateCheckoutSessionRequest request)
    {
        try
        {
            var createResult = await _paymentAppService.CreateCheckoutSessionAsync(request);
            if (!createResult.IsSuccess)
            {
                return this.ToBadRequest(createResult.Error, createResult.ErrorCode);
            }

            string url = createResult.Value!;
            return Ok(new { url });
        }
        catch (ArgumentException ex)
        {
            return this.ToBadRequest(ex.Message, ErrorCodes.ValidationError);
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
            var sessionResult = await _paymentAppService.GetCheckoutSessionAsync(sessionId);
            if (!sessionResult.IsSuccess)
            {
                return this.ToBadRequest(sessionResult.Error, sessionResult.ErrorCode);
            }

            var session = sessionResult.Value!;

            // Validate workspace ID from session metadata
            string workspaceIdStr = session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.WorkspaceId, string.Empty);
            if (!Guid.TryParse(workspaceIdStr, out Guid workspaceId))
            {
                return this.ToBadRequest(ApiMessageConstants.ErrorMessages.BillingWorkspaceIdNotInSessionMetadata, ErrorCodes.ValidationError);
            }

            // Verify the requesting user is a system admin or an Owner/Admin of this workspace.
            var isSystemAdmin =
                User.IsInRole(WorkspaceRoleConstants.SystemAdmin) ||
                User.IsInRole(WorkspaceRoleConstants.Admin);
            if (!isSystemAdmin)
            {
                var accessResult = await _workspaceClient.VerifyWorkspaceRolesAsync(
                    workspaceId,
                    userId.Value,
                    WorkspaceRoleConstants.Owner,
                    WorkspaceRoleConstants.Admin);
                if (!accessResult.IsSuccess || !accessResult.Value)
                {
                    return this.ToErrorResult(StatusCodes.Status403Forbidden, ApiMessageConstants.ErrorMessages.BillingAccessDeniedOwnerAdminRequired, ErrorCodes.Forbidden);
                }
            }

            // Fallback: process payment event inline if session already marked as paid
            if (session.PaymentStatus == PaymentConstants.Payments.StatusPaid)
            {
                bool isZeroDecimal = string.Equals(session.Currency, PaymentConstants.Currencies.Vnd, StringComparison.OrdinalIgnoreCase);
                decimal finalAmount = isZeroDecimal ? (session.AmountTotal ?? 0) : ((session.AmountTotal ?? 0) / 100m);

                var processResult = await _paymentAppService.ProcessPaymentEventAsync(new StripePaymentEventRequest(
                    StripeSessionId: session.Id,
                    PaymentIntentId: !string.IsNullOrEmpty(session.PaymentIntentId) ? session.PaymentIntentId : string.Empty,
                    Amount: finalAmount,
                    Currency: session.Currency,
                    UserIdStr: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.UserId, string.Empty),
                    WorkspaceIdStr: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.WorkspaceId, string.Empty),
                    PaymentType: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.PaymentType, string.Empty),
                    Status: PaymentConstants.Payments.StatusPaid,
                    PlanSlug: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.PlanSlug, string.Empty),
                    BillingCycle: session.Metadata.GetValueOrDefault(PaymentConstants.StripeMetadata.BillingCycle, string.Empty)
                ));
                if (!processResult.IsSuccess)
                {
                    return this.ToBadRequest(processResult.Error, processResult.ErrorCode);
                }
            }

            return Ok(session);
        }
        catch (ArgumentException ex)
        {
            return this.ToBadRequest(ex.Message, ErrorCodes.ValidationError);
        }
        catch (KeyNotFoundException ex)
        {
            return this.ToErrorResult(StatusCodes.Status404NotFound, ex.Message, ErrorCodes.NotFound);
        }
        catch (Exception ex)
        {
            return this.ToBadRequest(ex.Message, ErrorCodes.ValidationError);
        }
    }

    [HttpPost("webhook/stripe")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var stripeSignature = Request.Headers[BillingMessageConstants.Webhook.StripeSignatureHeader].ToString();

        try
        {
            var result = await _stripeWebhookService.HandleWebhookAsync(json, stripeSignature, HttpContext.RequestAborted);
            if (!result.IsSuccess)
            {
                return this.ToBadRequest(result.Error ?? ApiMessageConstants.ErrorMessages.BillingStripeWebhookFailed, ErrorCodes.InternalServerError);
            }

            return Ok();
        }
        catch (Stripe.StripeException ex)
        {
            return this.ToBadRequest(ex.Message, ErrorCodes.ValidationError);
        }
        catch (Exception ex)
        {
            return this.ToErrorResult(StatusCodes.Status500InternalServerError, ex.Message, ErrorCodes.InternalServerError);
        }
    }
}
