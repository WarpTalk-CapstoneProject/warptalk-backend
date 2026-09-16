using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceAuditLog;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Read-only, tenant-facing view of the platform audit log, scoped to one workspace and
/// restricted to its Owner and Admins.
/// </summary>
public interface IWorkspaceAuditLogService
{
    Task<Result<AdminPagedResult<WorkspaceAuditLogEntryDto>>> QueryAsync(
        Guid workspaceId,
        Guid userId,
        WorkspaceAuditLogQuery query,
        CancellationToken ct = default);
}
