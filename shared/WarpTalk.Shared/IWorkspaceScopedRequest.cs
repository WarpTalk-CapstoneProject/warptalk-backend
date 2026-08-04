using System;

namespace WarpTalk.Shared;

public interface IWorkspaceScopedRequest
{
    Guid WorkspaceId { get; }
}
