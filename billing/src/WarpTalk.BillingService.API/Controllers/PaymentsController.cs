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
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpPost]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdminSystem)]
    public async Task<ActionResult<PaymentTransactionDto>> CreatePayment([FromBody] CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var result = await _paymentService.CreatePaymentAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
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
                return BadRequest(new ApiErrorResponse(createResult.Error, createResult.ErrorCode));
            }

            string url = createResult.Value!;
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
            var isSystemAdmin =
                User.IsInRole(WorkspaceRoleConstants.SystemAdmin) ||
                User.IsInRole(WorkspaceRoleConstants.Admin);

            var result = await _paymentAppService.GetAndProcessCheckoutSessionAsync(sessionId, userId.Value, isSystemAdmin);
            
            if (!result.IsSuccess)
            {
                if (result.ErrorCode == ErrorCodes.Forbidden)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse(result.Error, result.ErrorCode));
                }
                return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
            }

            return Ok(result.Value);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiErrorResponse(ex.Message, ErrorCodes.ValidationError));
        }
        catch (KeyNotFoundException ex)
        {
            return StatusCode(StatusCodes.Status404NotFound, new ApiErrorResponse(ex.Message, ErrorCodes.NotFound));
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
        var stripeSignature = Request.Headers[BillingMessageConstants.Webhook.StripeSignatureHeader].ToString();

        try
        {
            var result = await _stripeWebhookService.HandleWebhookAsync(json, stripeSignature, HttpContext.RequestAborted);
            if (!result.IsSuccess)
            {
                return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingStripeWebhookFailed, ErrorCodes.InternalServerError));
            }

            return Ok();
        }
        catch (Stripe.StripeException ex)
        {
            return BadRequest(new ApiErrorResponse(ex.Message, ErrorCodes.ValidationError));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiErrorResponse(ex.Message, ErrorCodes.InternalServerError));
        }
    }
}
