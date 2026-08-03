using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Outbox;

public sealed class OutboxWorkspaceEventPublisher(WorkspaceOutboxWriter writer) : IWorkspaceEventPublisher
{
    public Task PublishWorkspaceCreatedAsync(
        Guid workspaceId,
        string name,
        string slug,
        Guid ownerUserId,
        CancellationToken ct = default)
    {
        var occurredAt = DateTime.UtcNow;
        var envelope = DomainEventEnvelope.Create(
            WorkspaceEventTypes.WorkspaceCreated,
            WorkspaceEventTypes.Producer,
            workspaceId.ToString(),
            new WorkspaceCreatedEventPayload(
                workspaceId.ToString(),
                name,
                slug,
                ownerUserId.ToString(),
                occurredAt),
            occurredAt: occurredAt);
        return writer.EnqueueAsync(envelope, "WorkspaceCreated", ct);
    }

    public Task PublishWorkspaceDeletedAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default)
    {
        var occurredAt = DateTime.UtcNow;
        var envelope = DomainEventEnvelope.Create(
            WorkspaceEventTypes.WorkspaceDeleted,
            WorkspaceEventTypes.Producer,
            workspaceId.ToString(),
            new WorkspaceDeletedEventPayload(
                workspaceId.ToString(),
                userId.ToString(),
                occurredAt,
                null),
            occurredAt: occurredAt);
        return writer.EnqueueAsync(envelope, "WorkspaceDeleted", ct);
    }

    public Task PublishMemberRemovedAsync(
        Guid workspaceId,
        Guid memberUserId,
        Guid removedByUserId,
        CancellationToken ct = default)
    {
        var occurredAt = DateTime.UtcNow;
        var envelope = DomainEventEnvelope.Create(
            WorkspaceEventTypes.MemberRemoved,
            WorkspaceEventTypes.Producer,
            workspaceId.ToString(),
            new MemberRemovedEventPayload(
                workspaceId.ToString(),
                memberUserId.ToString(),
                removedByUserId.ToString(),
                occurredAt),
            occurredAt: occurredAt);
        return writer.EnqueueAsync(envelope, "MemberRemoved", ct);
    }

    public Task PublishMemberRoleChangedAsync(
        Guid workspaceId,
        Guid targetUserId,
        string oldRole,
        string newRole,
        Guid changedByUserId,
        CancellationToken ct = default)
    {
        return PublishMemberRoleChangedAsync(
            workspaceId, targetUserId, oldRole, newRole, changedByUserId,
            Guid.NewGuid(), null, "Standard", "immediate", DateTime.UtcNow, null, ct);
    }

    public Task PublishMemberRoleChangedAsync(
        Guid workspaceId,
        Guid targetUserId,
        string oldRole,
        string newRole,
        Guid changedByUserId,
        Guid eventId,
        string? correlationId,
        CancellationToken ct = default)
    {
        return PublishMemberRoleChangedAsync(
            workspaceId, targetUserId, oldRole, newRole, changedByUserId,
            eventId, correlationId, "Standard", "immediate", DateTime.UtcNow, null, ct);
    }

    public Task PublishMemberRoleChangedAsync(
        Guid workspaceId,
        Guid targetUserId,
        string oldRole,
        string newRole,
        Guid changedByUserId,
        Guid eventId,
        string? correlationId,
        string membershipType,
        string effectiveBehavior,
        DateTime effectiveAt,
        string? idempotencyKey,
        CancellationToken ct = default)
    {
        var envelope = DomainEventEnvelope.Create(
            WorkspaceEventTypes.MemberRoleChanged,
            WorkspaceEventTypes.Producer,
            workspaceId.ToString(),
            new MemberRoleChangedEventPayload(
                workspaceId.ToString(),
                targetUserId.ToString(),
                oldRole,
                newRole,
                changedByUserId.ToString(),
                membershipType,
                effectiveBehavior,
                effectiveAt,
                idempotencyKey),
            correlationId: correlationId,
            occurredAt: effectiveAt);
        return writer.EnqueueAsync(envelope, "MemberRoleChanged", ct);
    }
}
