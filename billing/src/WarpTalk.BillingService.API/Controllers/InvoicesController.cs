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
    [WorkspaceAuthorize(Roles = "Owner, Admin")]
    public async Task<ActionResult<PaginatedResponse<InvoiceDto>>> GetWorkspaceInvoices(
        Guid workspaceId,
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.GetInvoicesAsync(workspaceId, query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    [HttpGet("global")]
    [AllowAnonymous]
    public async Task<ActionResult<PaginatedResponse<InvoiceDto>>> GetGlobalInvoices(
        [FromQuery] PaginationQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _invoiceService.GetGlobalInvoicesAsync(query, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result.ErrorCode, result.Error);

        return Ok(result.Value);
    }

    private ActionResult HandleFailure(string? errorCode, string? error) =>
        errorCode switch
        {
            ErrorCodes.BillingSubscriptionNotFound => NotFound(new ApiErrorResponse(error ?? "Subscription not found", errorCode)),
            _ => StatusCode(500, new ApiErrorResponse(error ?? "An unexpected error occurred", errorCode ?? ErrorCodes.InternalServerError))
        };
}
