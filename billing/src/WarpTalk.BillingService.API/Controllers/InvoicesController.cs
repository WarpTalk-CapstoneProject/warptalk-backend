using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.BillingService.API.Filters;

namespace WarpTalk.BillingService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/invoices")]
public class InvoicesController : ControllerBase
{
    private readonly IInvoiceService _invoiceService;

    public InvoicesController(IInvoiceService invoiceService)
    {
        _invoiceService = invoiceService;
    }

    /// <summary>
    /// Paginated invoice history for a workspace.
    /// </summary>
    [HttpGet("workspace/{workspaceId:guid}")]
    [RequireWorkspaceRole("Owner", "Admin")]
    public async Task<ActionResult<PagedResult<InvoiceDto>>> GetWorkspaceInvoices(
        Guid workspaceId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _invoiceService.GetInvoicesAsync(workspaceId, pageNumber, pageSize, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    /// <summary>
    /// Paginated global invoice history for admins.
    /// </summary>
    [HttpGet("global")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PagedResult<InvoiceDto>>> GetGlobalInvoices(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _invoiceService.GetGlobalInvoicesAsync(pageNumber, pageSize, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Ok(result.Value);
    }

    private ActionResult HandleFailure<T>(Result<T> result) =>
        result.ErrorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new { message = result.Error }),
            _ => StatusCode(500, new { message = result.Error })
        };
}
