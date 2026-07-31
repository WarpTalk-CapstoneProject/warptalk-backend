using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceEventPublisher
{
    Task PublishWorkspaceCreatedAsync(Guid workspaceId, string name, string slug, Guid ownerUserId, CancellationToken ct = default);
    Task PublishWorkspaceDeletedAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task PublishMemberRemovedAsync(Guid workspaceId, Guid memberUserId, Guid removedByUserId, CancellationToken ct = default);
    Task PublishMemberRoleChangedAsync(Guid workspaceId, Guid targetUserId, string oldRole, string newRole, Guid changedByUserId, CancellationToken ct = default);
    Task PublishMemberRoleChangedAsync(Guid workspaceId, Guid targetUserId, string oldRole, string newRole, Guid changedByUserId, Guid eventId, string? correlationId, CancellationToken ct = default);
    Task PublishMemberRoleChangedAsync(Guid workspaceId, Guid targetUserId, string oldRole, string newRole, Guid changedByUserId, Guid eventId, string? correlationId, string membershipType, string effectiveBehavior, DateTime effectiveAt, string? idempotencyKey, CancellationToken ct = default);
}
