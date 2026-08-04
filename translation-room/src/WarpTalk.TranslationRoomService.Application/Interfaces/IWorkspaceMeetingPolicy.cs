using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Asks WorkspaceService whether a caller is allowed to open a room in a workspace.
///
/// Translation rooms store only <see cref="Domain.Entities.TranslationRoom.WorkspaceId"/>, never the
/// workspace's per-member permissions, so the decision has to be made there. WorkspaceService has
/// always exposed a ValidateMeetingCreation RPC for exactly this — it was simply never called, which
/// is why a member whose host permission had been revoked could still create rooms (WT-249).
/// </summary>
public interface IWorkspaceMeetingPolicy
{
    /// <summary>
    /// Success when the caller may create the room; a Forbidden failure carrying the workspace's own
    /// reason otherwise.
    ///
    /// Unlike <see cref="IWorkspaceMemberDirectory"/>, which only ever widens a decision already
    /// denied on host identity, this one narrows: it is the whole permission gate. So it fails
    /// CLOSED — an unreachable WorkspaceService denies rather than allowing the create through, or
    /// an outage would become the very bypass this closes.
    /// </summary>
    Task<Result> ValidateMeetingCreationAsync(
        Guid workspaceId,
        Guid userId,
        IEnumerable<string> targetLanguages,
        CancellationToken ct = default);
}
