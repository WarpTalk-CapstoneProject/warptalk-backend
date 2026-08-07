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
    /// True when <paramref name="userId"/> is an active member of <paramref name="workspaceId"/> in
    /// any role, Owner/Admin included.
    ///
    /// The rooms LIST needs this and <see cref="IsOwnerOrAdminAsync"/> cannot answer it: a room's
    /// readability by a plain workspace member is not a host-adjacent privilege, it is the ordinary
    /// case. The list used to know only host / prior participant / personally-invited email, none of
    /// which a colleague who was simply added to the workspace satisfies, so every room in the
    /// workspace was absent from their list while the room itself opened for them by direct URL.
    ///
    /// Same contract as <see cref="IsOwnerOrAdminAsync"/>: implementations must never throw. An
    /// unreachable WorkspaceService returns false, which narrows the list back to the pre-existing
    /// host/participant/invitation answer rather than failing the request.
    /// </summary>
    Task<bool> IsActiveMemberAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default);
}
