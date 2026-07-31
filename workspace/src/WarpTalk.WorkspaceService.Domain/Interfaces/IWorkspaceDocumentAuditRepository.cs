using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceDocumentAuditRepository : IGenericRepository<WorkspaceDocumentAudit>
{
    Task<(List<WorkspaceDocumentAudit> Items, int TotalCount)> GetPagedAuditsAsync(
        Guid documentId,
        int page,
        int pageSize,
        bool isDescending = true,
        CancellationToken ct = default);

    Task<Dictionary<Guid, Guid?>> GetLatestApproverUserIdsByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default);
}
