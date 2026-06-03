using System;

namespace WarpTalk.Shared.Middleware;

public interface IWorkspaceContext
{
    Guid? UserId { get; }
    Guid? WorkspaceId { get; }
    string? Role { get; }
    string? MembershipType { get; }
    void SetContext(Guid userId, Guid workspaceId, string? role, string? membershipType);
}
