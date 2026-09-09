using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
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

    /// <summary>
    /// WT-260: this used [Authorize(Roles = ...)], which authorizes off JWT role claims.
    /// "Owner" and "Admin" are per-workspace membership resolved through workspace-service
    /// and never appear as claims in the token — the production seed grants ordinary accounts
    /// only the platform role 'user' — so a workspace Owner could never pass and the request
    /// 403'd before reaching any filter. Same fix already applied to Credits, Invoices,
    /// SalesInquiries, Subscriptions and Usages.
    /// </summary>
    [HttpGet("workspace/{workspaceId}/history")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
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

    /// <summary>
    /// Platform-admin only. <see cref="CreatePaymentRequest"/> carries no workspace id — it is
    /// keyed on a subscription — so there is nothing for the workspace-role filter to resolve;
    /// pointing it at this action would make it verify the caller's role against whichever Guid
    /// it found first. "Admin"/"admin" *are* real JWT platform roles, so authorizing off claims
    /// is correct here. Dropping "Owner" takes nothing away: it was never a token claim.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<PaymentTransactionDto>> CreatePayment([FromBody] CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var result = await _paymentService.CreatePaymentAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return StatusCode(201, result.Value);
    }

    /// <summary>
    /// WT-260, as above. This is the endpoint behind both plan checkout and credit top-up, so
    /// the JWT-role check meant every real workspace Owner got a 403 on both. The workspace id
    /// arrives in the body, which <see cref="RequireWorkspaceRoleAttribute"/> resolves through
    /// <see cref="WarpTalk.Shared.IWorkspaceScopedRequest"/>.
    /// </summary>
    [HttpPost("checkout")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateCheckoutSessionRequest request)
    {
        // WT-545. THE BUYER IS WHOEVER HOLDS THE TOKEN — the body does not get a say.
        //
        // UserId arrived from the client and went straight onto the Stripe session's metadata,
        // and that metadata is the very thing GetAndProcessCheckoutSessionAsync trusts to decide
        // "this caller is the buyer, let them through without a role check". So a request could
        // name somebody else as the buyer and mint a session that person was authorised to
        // complete. Overwriting it here is what makes the downstream check mean anything.
        //
        // The email goes with it so Stripe binds its hosted page to this account rather than
        // collecting whatever address the person holding the link types in.
        var buyerId = User.GetUserId();
        if (buyerId == null) return Unauthorized();

        request = request with
        {
            UserId = buyerId.Value,
            BuyerEmail = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
        };

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
                // 500, not 400. WT-370: the service only reports a failure here when a
                // REDELIVERY could still succeed, which makes this our fault, not a malformed
                // request from Stripe. The distinction is not pedantic — this status is what the
                // next person sees in the Stripe dashboard, and "400" points them at the sender.
                // Either way it is non-2xx, which is what re-arms Stripe's ~3 days of retries.
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingStripeWebhookFailed, ErrorCodes.InternalServerError));
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
