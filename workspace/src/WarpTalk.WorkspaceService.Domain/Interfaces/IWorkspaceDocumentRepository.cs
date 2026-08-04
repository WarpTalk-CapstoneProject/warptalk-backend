using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceDocumentRepository : IGenericRepository<WorkspaceDocument>
{
    Task<(List<WorkspaceDocument> Items, int TotalCount)> GetPagedDocumentsAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        bool isDescending = true,
        CancellationToken ct = default);
}
