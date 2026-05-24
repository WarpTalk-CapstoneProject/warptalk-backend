using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IWorkspaceRepository : IGenericRepository<Workspace>
{
    Task<(List<Workspace> Items, int TotalCount)> GetWorkspacesForUserAsync(Guid userId, int page, int pageSize, string? search = null, CancellationToken ct = default);
}
