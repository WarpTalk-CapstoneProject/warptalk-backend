using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;

namespace WarpTalk.WorkspaceService.Infrastructure.Caching;

public class WorkspaceCacheService : IWorkspaceCacheService
{
    private readonly IDistributedCache _cache;
    private const string ActiveWorkspaceKeyPrefix = "active_workspace:";

    public WorkspaceCacheService(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task SetActiveWorkspaceDetailsAsync(Guid userId, Guid workspaceId, string role, string membershipType, CancellationToken ct = default)
    {
        var cacheKey = $"{ActiveWorkspaceKeyPrefix}{userId}";
        var roleKey = $"{ActiveWorkspaceKeyPrefix}{userId}:role";
        var membershipTypeKey = $"{ActiveWorkspaceKeyPrefix}{userId}:membership_type";
        
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7)
        };
        await _cache.SetStringAsync(cacheKey, workspaceId.ToString(), options, ct);
        await _cache.SetStringAsync(roleKey, role, options, ct);
        await _cache.SetStringAsync(membershipTypeKey, membershipType, options, ct);
    }

    public async Task<Guid?> GetActiveWorkspaceAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = $"{ActiveWorkspaceKeyPrefix}{userId}";
        var cachedValue = await _cache.GetStringAsync(cacheKey, ct);
        
        if (!string.IsNullOrEmpty(cachedValue) && Guid.TryParse(cachedValue, out var workspaceId))
        {
            return workspaceId;
        }

        return null;
    }
}
