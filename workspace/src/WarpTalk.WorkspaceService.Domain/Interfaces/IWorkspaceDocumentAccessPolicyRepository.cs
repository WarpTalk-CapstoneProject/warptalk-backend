using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceDocumentAccessPolicyRepository : IGenericRepository<WorkspaceDocumentAccessPolicy>
{
    Task<(List<WorkspaceDocumentAccessPolicy> Items, int TotalCount)> GetPagedAccessPoliciesAsync(
        Guid documentId,
        int page,
        int pageSize,
        bool isDescending = true,
        CancellationToken ct = default);
}
