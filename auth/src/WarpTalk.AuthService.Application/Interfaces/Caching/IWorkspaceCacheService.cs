using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.AuthService.Application.Interfaces.Caching;

public interface IWorkspaceCacheService
{
    Task SetActiveWorkspaceAsync(Guid userId, Guid workspaceId, CancellationToken ct = default);
    Task<Guid?> GetActiveWorkspaceAsync(Guid userId, CancellationToken ct = default);
}
