using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Security;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/billing/checkout")]
public class CheckoutController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly IWorkspaceOwnershipResolver _workspaceOwnershipResolver;

    public CheckoutController(IPaymentService paymentService, IWorkspaceOwnershipResolver workspaceOwnershipResolver)
    {
        _paymentService = paymentService;
        _workspaceOwnershipResolver = workspaceOwnershipResolver;
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
            var ownerId = await _workspaceOwnershipResolver.ResolveOwnerUserIdAsync(workspaceId, cancellationToken);
            var response = await _paymentService.CreatePaymentLinkByOwnerAsync(ownerId, request, cancellationToken);
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

