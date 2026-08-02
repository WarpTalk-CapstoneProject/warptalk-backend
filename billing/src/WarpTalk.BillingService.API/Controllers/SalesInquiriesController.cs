using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.API.Extensions;
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
    public async Task<ActionResult<SalesInquiryDto>> Create(
        [FromBody] CreateSalesInquiryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess)
            return this.ToBadRequest(result.Error, result.ErrorCode);

        return CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value);
    }

    [Authorize]
    [HttpPost("workspace")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<SalesInquiryDto>> CreateWorkspace(
        [FromBody] CreateWorkspaceSalesInquiryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.CreateWorkspaceAsync(request, cancellationToken);
        if (!result.IsSuccess)
            return this.ToBadRequest(result.Error, result.ErrorCode);

        return CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<SalesInquiryDto>>> Get(
        [FromQuery] SalesInquiryQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.GetAsync(query, cancellationToken);
        return result.ToActionResult(this);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SalesInquiryDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.GetByIdAsync(id, cancellationToken);
        if (!result.IsSuccess && result.ErrorCode == ErrorCodes.NotFound)
            return NotFound(ControllerResultExtensions.ToErrorResponse(result.Error, result.ErrorCode));

        return result.ToActionResult(this);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<SalesInquiryDto>> UpdateStatus(
        Guid id,
        [FromBody] UpdateSalesInquiryStatusRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.UpdateStatusAsync(id, request, cancellationToken);
        if (!result.IsSuccess && result.ErrorCode == ErrorCodes.NotFound)
            return NotFound(ControllerResultExtensions.ToErrorResponse(result.Error, result.ErrorCode));

        return result.ToActionResult(this);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpPatch("{id:guid}/workspace")]
    public async Task<ActionResult<SalesInquiryDto>> LinkWorkspace(
        Guid id,
        [FromBody] LinkSalesInquiryWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.LinkWorkspaceAsync(id, request, cancellationToken);
        if (!result.IsSuccess && result.ErrorCode == ErrorCodes.NotFound)
            return NotFound(ControllerResultExtensions.ToErrorResponse(result.Error, result.ErrorCode));

        return result.ToActionResult(this);
    }

    [Authorize(Roles = WorkspaceRoleConstants.AdminSystem)]
    [HttpPost("{id:guid}/convert-to-contract")]
    public async Task<ActionResult<SalesInquiryDto>> ConvertToContract(
        Guid id,
        [FromBody] ConvertSalesInquiryToContractRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _salesInquiryService.ConvertToContractAsync(id, request, cancellationToken);
        if (!result.IsSuccess && result.ErrorCode == ErrorCodes.NotFound)
            return NotFound(ControllerResultExtensions.ToErrorResponse(result.Error, result.ErrorCode));

        return result.ToActionResult(this);
    }
}
