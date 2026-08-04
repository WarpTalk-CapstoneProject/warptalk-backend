using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Read side of the platform admin audit log plus the append path used by the
/// admin.action_recorded consumer (WT-210). There is no update or delete — the API cannot
/// modify history, and neither can the runtime database role.
/// </summary>
public interface IAdminAuditLogService
{
    Task<Result<AdminPagedResult<AdminAuditLogEntryDto>>> QueryAsync(
        AdminAuditLogQuery query,
        CancellationToken ct = default);

    Task<Result> RecordAsync(AdminActionRecordedEvent action, CancellationToken ct = default);
}
