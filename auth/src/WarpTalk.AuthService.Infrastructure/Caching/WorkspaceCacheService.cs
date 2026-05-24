using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using WarpTalk.AuthService.Application.Interfaces.Caching;

namespace WarpTalk.AuthService.Infrastructure.Caching;

public class WorkspaceCacheService : IWorkspaceCacheService
{
    private readonly IDistributedCache _cache;
    private const string ActiveWorkspaceKeyPrefix = "active_workspace:";

    public WorkspaceCacheService(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task SetActiveWorkspaceAsync(Guid userId, Guid workspaceId, CancellationToken ct = default)
    {
        var cacheKey = $"{ActiveWorkspaceKeyPrefix}{userId}";
        
        // Use SetStringAsync to avoid manual byte serialization and keep infrastructure logic encapsulated
        await _cache.SetStringAsync(cacheKey, workspaceId.ToString(), new DistributedCacheEntryOptions(), ct);
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
