using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Events;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Platform-wide sales lead inbox for the System Admin portal.
///
/// Every Enterprise / contact-sales request lands in subscription.sales_inquiries, but the only
/// read path was per workspace (and the public form has no workspace at all), so nobody on the
/// platform side could see a lead unless they already knew which workspace sent it.
///
/// Routed under ~/api/v1/admin/billing so the gateway's existing admin-billing route carries it —
/// no new admin prefix is exposed. Gated by the shared system-admin policy.
///
/// Deliberately NOT here: converting a lead into a contract subscription. That already exists in
/// SalesInquiryService.ConvertSalesInquiryToContractAsync and moves money-bearing state (it
/// cancels and creates subscriptions); wiring it to a button is its own decision.
/// </summary>
[ApiController]
[Route("api/v1/admin/billing/sales-leads")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminSalesLeadsController : ControllerBase
{
    private readonly ISalesInquiryService _salesInquiryService;
    private readonly ILogger<AdminSalesLeadsController> _logger;

    public AdminSalesLeadsController(
        ISalesInquiryService salesInquiryService,
        ILogger<AdminSalesLeadsController> logger)
    {
        _salesInquiryService = salesInquiryService;
        _logger = logger;
    }

    /// <summary>
    /// Every lead on the platform, newest first unless <paramref name="sort"/> says otherwise.
    /// </summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page, clamped by the service.</param>
    /// <param name="status">new | reviewing | quoted | converted | closed; null lists every status.</param>
    /// <param name="search">Matched against work email, company, first and last name.</param>
    /// <param name="workspaceId">Only leads linked to this workspace.</param>
    /// <param name="requestType">Exact request_type, case-insensitive.</param>
    /// <param name="source">Exact source (e.g. landing_pricing), case-insensitive.</param>
    /// <param name="createdFrom">Inclusive lower bound on created_at (UTC).</param>
    /// <param name="createdTo">Exclusive upper bound on created_at (UTC); earlier than createdFrom is a 400.</param>
    /// <param name="sort">created_desc (default) | created_asc | company_asc | company_desc; anything else is a 400.</param>
    /// <param name="ct">Request cancellation.</param>
    [HttpGet]
    public async Task<IActionResult> GetLeads(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] Guid? workspaceId = null,
        [FromQuery] string? requestType = null,
        [FromQuery] string? source = null,
        [FromQuery] DateTime? createdFrom = null,
        [FromQuery] DateTime? createdTo = null,
        [FromQuery] string? sort = null,
        CancellationToken ct = default)
    {
        var result = await _salesInquiryService.GetSalesInquiriesAsync(
            new SalesInquiryQuery(
                page,
                pageSize,
                status,
                search,
                workspaceId,
                NewestFirst: true,
                RequestType: requestType,
                Source: source,
                CreatedFrom: createdFrom,
                CreatedTo: createdTo,
                Sort: sort),
            ct);
        return ToActionResult(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetLead(Guid id, CancellationToken ct)
    {
        var result = await _salesInquiryService.GetSalesInquiryByIdAsync(id, ct);
        return ToActionResult(result);
    }

    /// <summary>Move a lead through new → reviewing → quoted → converted / closed.</summary>
    [HttpPatch("{id:guid}/status")]
    [AdminAudited(AdminAuditBillingActions.SalesLeadStatusChanged, AdminAuditEntityTypes.SalesLead, typeof(SalesInquiry), EntityRouteKey = "id")]
    public async Task<IActionResult> UpdateStatus(
        Guid id,
        [FromBody] UpdateSalesInquiryStatusRequest request,
        CancellationToken ct)
    {
        var adminUserId = User.GetUserId();
        if (adminUserId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _salesInquiryService.UpdateSalesInquiryStatusAsync(id, request, ct);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "System admin {AdminUserId} set sales inquiry {SalesInquiryId} to status {Status}",
                adminUserId, id, result.Value!.Status);
        }

        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
