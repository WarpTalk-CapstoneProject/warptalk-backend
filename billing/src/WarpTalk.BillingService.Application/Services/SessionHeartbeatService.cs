using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class SessionHeartbeatService : ISessionHeartbeatService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SessionHeartbeatService> _logger;
    private readonly IRedisBillingStore _redisStore;

    public SessionHeartbeatService(IUnitOfWork unitOfWork, ILogger<SessionHeartbeatService> logger, IRedisBillingStore redisStore)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _redisStore = redisStore;
    }

    public async Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var sub = await _unitOfWork.SubscriptionRepository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null,
            cancellationToken);

        if (sub is null)
            return Result.Failure<Guid>("Subscription not found.", ErrorCodes.BillingSubscriptionNotFound);

        var sessionId = Guid.NewGuid();
        // 15s active TTL + 60s Grace Period = 75s total TTL
        await _redisStore.SetSessionActiveAsync(sessionId, TimeSpan.FromSeconds(75), cancellationToken);

        return Result.Success(sessionId);
    }

    public async Task<Result<bool>> ProcessHeartbeatAsync(Guid sessionId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        // Just refresh the TTL in Redis (15s active + 60s grace = 75s)
        await _redisStore.SetSessionActiveAsync(sessionId, TimeSpan.FromSeconds(75), cancellationToken);

        return Result.Success(true);
    }
}
