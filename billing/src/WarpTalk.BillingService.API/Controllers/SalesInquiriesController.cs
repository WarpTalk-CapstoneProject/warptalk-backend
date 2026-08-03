using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.API.Controllers;

[ApiController]
[Route("api/v1/sales-inquiries")]
public class SalesInquiriesController : ControllerBase
{
    private readonly ISalesInquiryService _salesInquiryService;

    public SalesInquiriesController(ISalesInquiryService salesInquiryService)
    {
        _salesInquiryService = salesInquiryService;
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<ActionResult<SalesInquiryDto>> SubmitPublicSalesInquiry(
        [FromBody] CreateSalesInquiryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.CreatePublicInquiryAsync(request, cancellationToken);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return CreatedAtAction(nameof(GetSalesInquiryById), new { id = result.Value!.Id }, result.Value);
    }

    [Authorize]
    [HttpPost("workspace")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<SalesInquiryDto>> SubmitWorkspaceSalesInquiry(
        [FromBody] CreateWorkspaceSalesInquiryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.CreateWorkspaceInquiryAsync(request, cancellationToken);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return CreatedAtAction(nameof(GetSalesInquiryById), new { id = result.Value!.Id }, result.Value);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<SalesInquiryDto>>> GetSalesInquiries(
        [FromQuery] SalesInquiryQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.GetSalesInquiriesAsync(query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SalesInquiryDto>> GetSalesInquiryById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.GetSalesInquiryByIdAsync(id, cancellationToken);
        if (!result.IsSuccess && result.ErrorCode == ErrorCodes.NotFound)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<SalesInquiryDto>> UpdateSalesInquiryStatus(
        Guid id,
        [FromBody] UpdateSalesInquiryStatusRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.UpdateSalesInquiryStatusAsync(id, request, cancellationToken);
        if (!result.IsSuccess && result.ErrorCode == ErrorCodes.NotFound)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpPatch("{id:guid}/workspace")]
    public async Task<ActionResult<SalesInquiryDto>> LinkSalesInquiryWorkspace(
        Guid id,
        [FromBody] LinkSalesInquiryWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.LinkSalesInquiryWorkspaceAsync(id, request, cancellationToken);
        if (!result.IsSuccess && result.ErrorCode == ErrorCodes.NotFound)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpPost("{id:guid}/convert-to-contract")]
    public async Task<ActionResult<SalesInquiryDto>> ConvertSalesInquiryToContract(
        Guid id,
        [FromBody] ConvertSalesInquiryToContractRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.ConvertSalesInquiryToContractAsync(id, request, cancellationToken);
        if (!result.IsSuccess && result.ErrorCode == ErrorCodes.NotFound)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }
}
