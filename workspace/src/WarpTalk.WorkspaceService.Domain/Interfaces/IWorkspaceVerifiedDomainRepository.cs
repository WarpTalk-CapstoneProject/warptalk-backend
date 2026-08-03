using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceVerifiedDomainRepository : IGenericRepository<WorkspaceVerifiedDomain>
{
    Task<(List<WorkspaceVerifiedDomain> Items, int TotalCount)> GetPagedVerifiedDomainsAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        bool isDescending = true,
        CancellationToken ct = default);
}
