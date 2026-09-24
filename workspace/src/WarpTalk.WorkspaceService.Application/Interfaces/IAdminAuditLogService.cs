using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Read side of the platform admin audit log plus the append path every service records through
/// (WT-210). There is no update or delete — the API cannot modify history, and neither can the
/// runtime database role.
/// </summary>
public interface IAdminAuditLogService
{
    /// <summary>One page, newest first, every filter applied in the database.</summary>
    Task<Result<AdminAuditLogPageDto>> SearchAsync(AdminAuditLogQuery query, CancellationToken ct = default);

    Task<Result<AdminAuditLogEntryDto>> GetAsync(Guid id, CancellationToken ct = default);

    Task<Result<AdminAuditLogFacetsDto>> GetFacetsAsync(CancellationToken ct = default);

    /// <summary>
    /// The filtered log as CSV, capped at <c>MaxExportRows</c>. The export is itself an admin
    /// action and is recorded before the file is returned; one that cannot be recorded is refused.
    /// </summary>
    Task<Result<AdminAuditLogExport>> ExportCsvAsync(
        AdminAuditLogQuery query,
        AdminActorContext actor,
        CancellationToken ct = default);

    Task<Result> RecordAsync(AdminActionRecordedEvent action, CancellationToken ct = default);
}
