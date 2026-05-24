using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ISessionHeartbeatService
{
    Task<Result<bool>> ProcessHeartbeatAsync(Guid sessionId, Guid workspaceId, CancellationToken cancellationToken = default);

    Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}
