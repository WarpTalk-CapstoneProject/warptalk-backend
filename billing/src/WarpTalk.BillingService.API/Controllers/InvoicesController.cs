using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
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
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<PaginatedResponse<InvoiceDto>>> GetWorkspaceInvoices(
        Guid workspaceId,
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.GetInvoicesAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpGet("global")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    public async Task<ActionResult<PaginatedResponse<InvoiceDto>>> GetGlobalInvoices(
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.GetGlobalInvoicesAsync(query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
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
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
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
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }
}
