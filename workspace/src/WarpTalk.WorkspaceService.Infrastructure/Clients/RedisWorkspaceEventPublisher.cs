using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public class RedisWorkspaceEventPublisher : IWorkspaceEventPublisher
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisWorkspaceEventPublisher> _logger;

    public RedisWorkspaceEventPublisher(IConnectionMultiplexer redis, ILogger<RedisWorkspaceEventPublisher> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task PublishWorkspaceDeletedAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-events", new NameValueEntry[]
            {
                new NameValueEntry("event_type", "WorkspaceDeleted"),
                new NameValueEntry("workspace_id", workspaceId.ToString()),
                new NameValueEntry("deleted_by", userId.ToString()),
                new NameValueEntry("deleted_at", DateTime.UtcNow.ToString("o"))
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish workspace delete event to Redis Stream. WorkspaceId: {WorkspaceId}", workspaceId);
        }
    }
}
