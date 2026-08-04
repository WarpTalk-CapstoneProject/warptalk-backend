using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// Read-only query over the platform admin audit log (WT-210).
///
/// Query only, by design: there is no POST, PUT, PATCH, or DELETE here, so no administrator
/// can edit or erase an entry through the Admin API. Entries arrive either from workspace
/// lifecycle actions in this service or from admin.action_recorded events published by other
/// services, which own separate logical databases.
/// </summary>
[ApiController]
[Route("api/v1/admin/audit-log")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminAuditLogController : ControllerBase
{
    private readonly IAdminAuditLogService _adminAuditLogService;

    public AdminAuditLogController(IAdminAuditLogService adminAuditLogService)
    {
        _adminAuditLogService = adminAuditLogService;
    }

    [HttpGet]
    public async Task<IActionResult> Query([FromQuery] AdminAuditLogQuery query, CancellationToken ct)
    {
        var result = await _adminAuditLogService.QueryAsync(query, ct);
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
