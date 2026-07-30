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

    public async Task PublishMemberRoleChangedAsync(Guid workspaceId, Guid targetUserId, string oldRole, string newRole, Guid changedByUserId, CancellationToken ct = default)
    {
        try
        {
            await _publishEndpoint.Publish(new { EventType = "WorkspaceMemberRoleChanged.v1", WorkspaceId = workspaceId, TargetUserId = targetUserId, OldRole = oldRole, NewRole = newRole, ChangedByUserId = changedByUserId, OccurredAt = DateTime.UtcNow }, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to publish role change event. WorkspaceId: {WorkspaceId}", workspaceId); }
    }

    public async Task PublishMemberRoleChangedAsync(Guid workspaceId, Guid targetUserId, string oldRole, string newRole, Guid changedByUserId, Guid eventId, string? correlationId, CancellationToken ct = default)
    {
        try
        {
            await _publishEndpoint.Publish(new
            {
                EventType = "WorkspaceMemberRoleChanged.v1",
                EventId = eventId,
                WorkspaceId = workspaceId,
                TargetUserId = targetUserId,
                OldRole = oldRole,
                NewRole = newRole,
                ChangedByUserId = changedByUserId,
                CorrelationId = correlationId,
                OccurredAt = DateTime.UtcNow,
                EffectiveBehavior = "next-request-or-session"
            }, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to publish role change event. WorkspaceId: {WorkspaceId}", workspaceId); }
    }

    public async Task PublishMemberRoleChangedAsync(Guid workspaceId, Guid targetUserId, string oldRole, string newRole, Guid changedByUserId, Guid eventId, string? correlationId, string membershipType, string effectiveBehavior, DateTime effectiveAt, string? idempotencyKey, CancellationToken ct = default)
    {
        try
        {
            await _publishEndpoint.Publish(new
            {
                EventType = "WorkspaceMemberRoleChanged.v1",
                EventId = eventId,
                WorkspaceId = workspaceId,
                TargetUserId = targetUserId,
                OldRole = oldRole,
                NewRole = newRole,
                MembershipType = membershipType,
                ChangedByUserId = changedByUserId,
                CorrelationId = correlationId,
                IdempotencyKey = idempotencyKey,
                OccurredAt = DateTime.UtcNow,
                EffectiveAt = effectiveAt,
                EffectiveBehavior = effectiveBehavior
            }, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to publish role change event. WorkspaceId: {WorkspaceId}", workspaceId); }
    }

}
