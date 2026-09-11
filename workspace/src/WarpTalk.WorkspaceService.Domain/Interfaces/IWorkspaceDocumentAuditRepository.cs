using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceDocumentAuditRepository : IGenericRepository<WorkspaceDocumentAudit>
{
    /// <param name="excludeActions">
    /// Actions to leave out of both the page and its TotalCount — the document-history route drops
    /// GetDocumentDetails, which is written on every single read and would otherwise bury the
    /// decisions.
    /// </param>
    Task<(List<WorkspaceDocumentAudit> Items, int TotalCount)> GetPagedAuditsAsync(
        Guid documentId,
        int page,
        int pageSize,
        bool isDescending = true,
        IReadOnlyCollection<string>? excludeActions = null,
        CancellationToken ct = default);

    /// <summary>
    /// The most recent audit row for one document and one action, or null. WT-633.
    /// </summary>
    Task<WorkspaceDocumentAudit?> GetLatestActionAsync(
        Guid documentId,
        string action,
        CancellationToken ct = default);

    Task<Dictionary<Guid, Guid?>> GetLatestApproverUserIdsByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default);
}
