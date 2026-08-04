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

        return Created($"/api/v1/sales-inquiries/workspace/{request.WorkspaceId}", result.Value);
    }

    [Authorize]
    [HttpGet("workspace/{workspaceId:guid}")]
    [RequireWorkspaceRole(WorkspaceRoleConstants.Owner, WorkspaceRoleConstants.Admin, WorkspaceRoleConstants.SystemAdmin)]
    public async Task<ActionResult<PaginatedResponse<SalesInquiryDto>>> GetWorkspaceSalesInquiries(
        Guid workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = new SalesInquiryQuery(page, pageSize, WorkspaceId: workspaceId);
        var result = await _salesInquiryService.GetSalesInquiriesAsync(query, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError, result.ErrorCode));
        }
        return Ok(result.Value);
    }
}
