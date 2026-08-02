using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Extensions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class InvoicesController : ControllerBase
{
    private readonly IInvoiceService _invoiceService;

    public InvoicesController(IInvoiceService invoiceService)
    {
        _invoiceService = invoiceService;
    }

    [HttpGet("workspace/{workspaceId}")]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdminSystem)]
    public async Task<ActionResult<PaginatedResponse<InvoiceDto>>> GetWorkspaceInvoices(
        Guid workspaceId,
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.GetInvoicesAsync(workspaceId, query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("global")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<PaginatedResponse<InvoiceDto>>> GetGlobalInvoices(
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.GetGlobalInvoicesAsync(query, cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPost("{invoiceId}/checkout")]
    [Authorize(Roles = WorkspaceRoleConstants.OwnerAdminSystem)]
    public async Task<ActionResult<object>> CreateInvoiceCheckout(
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.CreateInvoiceCheckoutSessionAsync(invoiceId, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return Ok(new { url = result.Value });
    }

    [HttpPost("{invoiceId}/mark-paid")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<InvoiceDto>> MarkInvoicePaid(
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.MarkInvoicePaidAsync(invoiceId, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToBadRequest(result.Error, result.ErrorCode);
        }

        return Ok(result.Value);
    }
}
