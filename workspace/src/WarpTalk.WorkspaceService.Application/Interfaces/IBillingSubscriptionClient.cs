using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IBillingSubscriptionClient
{
    Task<bool> IsWorkspaceOnActiveTrialAsync(Guid workspaceId, CancellationToken ct = default);
}
