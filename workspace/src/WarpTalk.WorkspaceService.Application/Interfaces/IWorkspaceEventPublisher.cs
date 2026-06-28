using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceEventPublisher
{
    Task PublishWorkspaceDeletedAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
}
