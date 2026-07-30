using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceEventPublisher
{
    Task PublishWorkspaceCreatedAsync(Guid workspaceId, string name, string slug, Guid ownerUserId, CancellationToken ct = default);
    Task PublishWorkspaceDeletedAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task PublishMemberRemovedAsync(Guid workspaceId, Guid memberUserId, Guid removedByUserId, CancellationToken ct = default);
}
