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
[Route("api/v1/[controller]")]
public class InvoicesController : ControllerBase
{
    private readonly IInvoiceService _invoiceService;

    public InvoicesController(IInvoiceService invoiceService)
    {
        _invoiceService = invoiceService;
    }

    [HttpGet("workspace/{workspaceId}")]
    [Authorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<PaginatedResponse<InvoiceDto>>> GetWorkspaceInvoices(
        Guid workspaceId,
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.GetInvoicesAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [HttpGet("global")]
    [AllowAnonymous]
    public async Task<ActionResult<PaginatedResponse<InvoiceDto>>> GetGlobalInvoices(
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.GetGlobalInvoicesAsync(query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }
}