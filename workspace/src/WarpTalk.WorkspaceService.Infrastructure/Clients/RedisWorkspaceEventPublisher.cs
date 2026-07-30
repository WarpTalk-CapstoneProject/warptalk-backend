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

    public async Task PublishWorkspaceCreatedAsync(Guid workspaceId, string name, string slug, Guid ownerUserId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-events", new NameValueEntry[]
            {
                new("event_type", "WorkspaceCreated"),
                new("workspace_id", workspaceId.ToString()),
                new("name", name),
                new("slug", slug),
                new("owner_user_id", ownerUserId.ToString()),
                new("created_at", DateTime.UtcNow.ToString("o"))
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish workspace created event to Redis Stream. WorkspaceId: {WorkspaceId}", workspaceId);
        }
    }

    public async Task PublishWorkspaceDeletedAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-events", new NameValueEntry[]
            {
                new("event_type", "WorkspaceDeleted"),
                new("workspace_id", workspaceId.ToString()),
                new("deleted_by", userId.ToString()),
                new("deleted_at", DateTime.UtcNow.ToString("o"))
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish workspace delete event to Redis Stream. WorkspaceId: {WorkspaceId}", workspaceId);
        }
    }

    public async Task PublishMemberRemovedAsync(Guid workspaceId, Guid memberUserId, Guid removedByUserId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StreamAddAsync("workspace-events", new NameValueEntry[]
            {
                new("event_type", "MemberRemoved"),
                new("workspace_id", workspaceId.ToString()),
                new("user_id", memberUserId.ToString()),
                new("removed_by", removedByUserId.ToString()),
                new("removed_at", DateTime.UtcNow.ToString("o"))
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish member removed event to Redis Stream. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, memberUserId);
        }
    }

    public Task PublishMemberRoleChangedAsync(Guid workspaceId, Guid targetUserId, string oldRole, string newRole, Guid changedByUserId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task PublishMemberRoleChangedAsync(Guid workspaceId, Guid targetUserId, string oldRole, string newRole, Guid changedByUserId, Guid eventId, string? correlationId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task PublishMemberRoleChangedAsync(Guid workspaceId, Guid targetUserId, string oldRole, string newRole, Guid changedByUserId, Guid eventId, string? correlationId, string membershipType, string effectiveBehavior, DateTime effectiveAt, string? idempotencyKey, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
