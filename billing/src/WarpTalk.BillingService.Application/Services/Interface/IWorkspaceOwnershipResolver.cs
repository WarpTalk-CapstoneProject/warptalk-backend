using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Services.Interface;
public interface IWorkspaceOwnershipResolver
{
    Task<Guid> ResolveOwnerUserIdAsync(Guid workspaceId, CancellationToken ct = default);

    Task<bool> IsOwnerAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);

    Task<bool> HasAccessAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
}