using System;

namespace WarpTalk.Shared.Middleware;

public interface IWorkspaceContext
{
    Guid? UserId { get; }
    Guid? WorkspaceId { get; }
    void SetContext(Guid userId, Guid workspaceId);
}
