using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Security;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/checkout")]
public class CheckoutController : ControllerBase
{
    private readonly IPaymentService _paymentService;

    public CheckoutController(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <summary>
    /// Tạo Link Thanh Toán (PayOS) - POST /api/v1/billing/checkout
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreatePaymentLink(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromBody] CreatePaymentLinkRequest request,
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

        try
        {
            var response = await _paymentService.CreatePaymentLinkAsync(workspaceId, request, cancellationToken);
            return Ok(response);
        }
        catch (Exception)
        {
            return BadRequest(new
            {
                message = "Failed to create payment link.",
                traceId = HttpContext.TraceIdentifier
            });
        }
    }
}

