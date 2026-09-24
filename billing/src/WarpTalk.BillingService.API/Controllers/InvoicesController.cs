using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Events;
using WarpTalk.BillingService.Domain.Entities;

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

    /// <summary>
    /// WT-260: plain [Authorize], deliberately. A workspace Owner is not a JWT claim, and the only
    /// route value is an invoice id, which RequireWorkspaceRole would mistake for a workspace id.
    /// The service resolves the invoice's workspace and checks the caller's role there.
    /// </summary>
    [HttpPost("{invoiceId}/checkout")]
    public async Task<ActionResult<object>> CreateInvoiceCheckout(
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var caller = new InvoiceCheckoutCaller(
            userId.Value,
            User.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
            User.IsInRole(WorkspaceRoleConstants.SystemAdmin) || User.IsInRole(WorkspaceRoleConstants.Admin));

        var result = await _invoiceService.CreateInvoiceCheckoutSessionAsync(invoiceId, caller, cancellationToken);
        if (!result.IsSuccess)
        {
            var error = new ApiErrorResponse(
                result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
                result.ErrorCode);
            return result.ErrorCode switch
            {
                ErrorCodes.NotFound => NotFound(error),
                ErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, error),
                ErrorCodes.InternalServerError => StatusCode(StatusCodes.Status500InternalServerError, error),
                _ => BadRequest(error),
            };
        }

        return Ok(new { url = result.Value });
    }

    [HttpPost("{invoiceId}/mark-paid")]
    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [AdminAudited(AdminAuditWorkspaceActions.InvoiceMarkedPaid, AdminAuditEntityTypes.Invoice, typeof(Invoice), EntityRouteKey = "invoiceId")]
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
