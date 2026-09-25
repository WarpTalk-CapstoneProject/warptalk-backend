using System;
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
/// The platform admin audit log (WT-210): one query API over the store every service records
/// into — workspace lifecycle and admin-page actions here, and over gRPC from auth, billing, the
/// language catalog, the plugin catalog, the global glossary and announcements.
///
/// Read-only by design: there is no POST, PUT, PATCH or DELETE, so no administrator can edit or
/// erase an entry through the Admin API. The one write this controller causes is the record of an
/// export, which is itself an admin action.
/// </summary>
[ApiController]
[Route("api/v1/admin/audit-log")]
public class AdminAuditLogController : ControllerBase
{
    private readonly IAdminAuditLogService _adminAuditLogService;

    public AdminAuditLogController(IAdminAuditLogService adminAuditLogService)
    {
        _adminAuditLogService = adminAuditLogService;
    }

    /// <summary>
    /// One page, newest first. Filters: from, to, actorId, action, entityType, entityId (a GUID or
    /// a natural key), workspaceId, sourceService, result, q. Page with <c>cursor</c> = the previous
    /// page's <c>nextCursor</c>.
    /// </summary>
    [HttpGet]
    [RequirePermission(AdminPermissions.AuditRead)]
    public async Task<IActionResult> Query([FromQuery] AdminAuditLogQuery query, CancellationToken ct)
        => ToActionResult(await _adminAuditLogService.SearchAsync(query, ct));

    /// <summary>The values present in the store for each filter, with counts and actor names.</summary>
    [HttpGet("facets")]
    [RequirePermission(AdminPermissions.AuditRead)]
    public async Task<IActionResult> Facets(CancellationToken ct)
        => ToActionResult(await _adminAuditLogService.GetFacetsAsync(ct));

    /// <summary>The filtered log as CSV (same filters as the list, no cursor), at most 10,000 rows.</summary>
    [HttpGet("export")]
    [RequirePermission(AdminPermissions.AuditExport)]
    public async Task<IActionResult> Export([FromQuery] AdminAuditLogQuery query, CancellationToken ct)
    {
        if (!AdminActorContext.TryResolve(User, HttpContext, out var actor))
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _adminAuditLogService.ExportCsvAsync(query, actor, ct);
        if (!result.IsSuccess) return ToActionResult(result);

        var export = result.Value!;
        Response.Headers["X-Audit-Export-Rows"] = export.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Audit-Export-Truncated"] = export.Truncated ? "true" : "false";
        return File(export.Content, "text/csv; charset=utf-8", export.FileName);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(AdminPermissions.AuditRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => ToActionResult(await _adminAuditLogService.GetAsync(id, ct));

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
