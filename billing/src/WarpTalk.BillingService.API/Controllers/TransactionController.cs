using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Security;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;

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

    // ===================================================
    // CREATE PAYMENT LINK (WORKS WITH YOUR INTERFACE)
    // ===================================================
    [HttpPost("create-link")]
    public async Task<IActionResult> CreatePaymentLink(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromBody] CreatePaymentLinkRequest request,
        CancellationToken ct)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null)
            return accessError;

        var result = await _paymentService.CreatePaymentLinkAsync(workspaceId, request, ct);
        return Ok(result);
    }

    // ===================================================
    // CREATE PAYMENT LINK BY OWNER
    // ===================================================
    [HttpPost("create-link-owner")]
    public async Task<IActionResult> CreatePaymentLinkByOwner(
        [FromHeader(Name = "X-Workspace-Id")] Guid workspaceId,
        [FromBody] CreatePaymentLinkRequest request,
        CancellationToken ct)
    {
        var accessError = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(HttpContext, workspaceId);
        if (accessError != null)
            return accessError;

        var result = await _paymentService.CreatePaymentLinkByOwnerAsync(workspaceId, request, ct);
        return Ok(result);
    }

    // ===================================================
    // PAYOS WEBHOOK
    // ===================================================
    [HttpPost("webhook/payos")]
    public async Task<IActionResult> PayOsWebhook(
        [FromBody] PayOsWebhookPayload payload,
        CancellationToken ct)
    {
        var result = await _paymentService.ProcessPayOsWebhookAsync(payload, ct);
        return Ok(result);
    }
}