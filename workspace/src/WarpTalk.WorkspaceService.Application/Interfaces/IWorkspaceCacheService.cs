using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces.Caching;
//check which wp active to external user(personal mail)
public interface IWorkspaceCacheService
{
    Task SetActiveWorkspaceDetailsAsync(Guid userId, Guid workspaceId, string role, string membershipType, CancellationToken ct = default);
    Task<Guid?> GetActiveWorkspaceAsync(Guid userId, CancellationToken ct = default);
}
