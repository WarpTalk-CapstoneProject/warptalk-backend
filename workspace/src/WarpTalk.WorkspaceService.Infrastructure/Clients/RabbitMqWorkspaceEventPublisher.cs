using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public class RabbitMqWorkspaceEventPublisher : IWorkspaceEventPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<RabbitMqWorkspaceEventPublisher> _logger;

    public RabbitMqWorkspaceEventPublisher(IPublishEndpoint publishEndpoint, ILogger<RabbitMqWorkspaceEventPublisher> logger)
    {
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task PublishWorkspaceCreatedAsync(Guid workspaceId, string name, string slug, Guid ownerUserId, CancellationToken ct = default)
    {
        try
        {
            var message = new WorkspaceCreatedEvent
            {
                WorkspaceId = workspaceId.ToString(),
                Name = name,
                Slug = slug,
                OwnerUserId = ownerUserId.ToString(),
            };
            await _publishEndpoint.Publish(message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish WorkspaceCreatedEvent to RabbitMQ. WorkspaceId: {WorkspaceId}", workspaceId);
        }
    }

    public async Task PublishWorkspaceDeletedAsync(Guid workspaceId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var message = new WorkspaceDeletedEvent
            {
                WorkspaceId = workspaceId.ToString(),
                DeletedByUserId = userId.ToString(),
                DeletedAt = DateTime.UtcNow,
            };
            await _publishEndpoint.Publish(message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish WorkspaceDeletedEvent to RabbitMQ. WorkspaceId: {WorkspaceId}", workspaceId);
        }
    }

    public async Task PublishMemberRemovedAsync(Guid workspaceId, Guid memberUserId, Guid removedByUserId, CancellationToken ct = default)
    {
        try
        {
            var message = new MemberRemovedEvent
            {
                WorkspaceId = workspaceId.ToString(),
                UserId = memberUserId.ToString(),
                RemovedByUserId = removedByUserId.ToString(),
                RemovedAt = DateTime.UtcNow,
            };
            await _publishEndpoint.Publish(message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish MemberRemovedEvent to RabbitMQ. WorkspaceId: {WorkspaceId}, UserId: {UserId}", workspaceId, memberUserId);
        }
    }
}
