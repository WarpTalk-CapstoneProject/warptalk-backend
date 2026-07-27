using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public class HybridWorkspaceEventPublisher : IWorkspaceEventPublisher
{
    private readonly RabbitMqWorkspaceEventPublisher _rabbitMqPublisher;
    private readonly RedisWorkspaceEventPublisher _redisPublisher;

    public HybridWorkspaceEventPublisher(
        IConnectionMultiplexer redis,
        IPublishEndpoint publishEndpoint,
        ILogger<RedisWorkspaceEventPublisher> redisLogger,
        ILogger<RabbitMqWorkspaceEventPublisher> rabbitLogger)
    {
        _rabbitMqPublisher = new RabbitMqWorkspaceEventPublisher(publishEndpoint, rabbitLogger);
        _redisPublisher = new RedisWorkspaceEventPublisher(redis, redisLogger);
    }

    public async Task PublishWorkspaceCreatedAsync(Guid workspaceId, string name, string slug, Guid ownerUserId, CancellationToken ct = default)
    {
        await _rabbitMqPublisher.PublishWorkspaceCreatedAsync(workspaceId, name, slug, ownerUserId, ct);
        await _redisPublisher.PublishWorkspaceCreatedAsync(workspaceId, name, slug, ownerUserId, ct);
    }

    public async Task PublishWorkspaceDeletedAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        await _rabbitMqPublisher.PublishWorkspaceDeletedAsync(workspaceId, userId, ct);
        await _redisPublisher.PublishWorkspaceDeletedAsync(workspaceId, userId, ct);
    }

    public async Task PublishMemberRemovedAsync(Guid workspaceId, Guid memberUserId, Guid removedByUserId, CancellationToken ct = default)
    {
        await _rabbitMqPublisher.PublishMemberRemovedAsync(workspaceId, memberUserId, removedByUserId, ct);
        await _redisPublisher.PublishMemberRemovedAsync(workspaceId, memberUserId, removedByUserId, ct);
    }
}
