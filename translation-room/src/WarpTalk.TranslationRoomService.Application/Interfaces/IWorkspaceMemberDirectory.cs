namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Read-only view of a caller's workspace membership, resolved from WorkspaceService over gRPC.
/// Translation rooms only store <see cref="Domain.Entities.TranslationRoom.WorkspaceId"/>, never the
/// workspace's member roles, so host-adjacent authorization (WT-188: a workspace Owner/Admin may
/// admit participants into any room in their workspace, not just rooms they personally host) has to
/// ask WorkspaceService for the caller's role.
/// </summary>
public interface IWorkspaceMemberDirectory
{
    /// <summary>
    /// True when <paramref name="userId"/> is an active Owner or Admin of <paramref name="workspaceId"/>.
    /// Implementations must never throw: an unreachable/failing WorkspaceService returns false so the
    /// caller falls back to its own host check rather than failing the whole request.
    /// </summary>
    Task<bool> IsOwnerOrAdminAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// True when <paramref name="userId"/> is an ACTIVE member of <paramref name="workspaceId"/>,
    /// any role. WT-433: the join-by-id path (a shared room LINK) is gated on this — a link is a
    /// weaker credential than a room code, so mere possession is not enough. Same never-throws
    /// posture as IsOwnerOrAdminAsync: an unreachable WorkspaceService answers false.
    /// </summary>
    Task<bool> IsMemberAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default);
}
