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

    /// <summary>
    /// The tenant kill switch: success while the workspace is live, a Forbidden failure once a
    /// system admin has suspended (or soft-deleted) it.
    ///
    /// Separate from <see cref="ValidateMeetingCreationAsync"/> because it answers a different
    /// question and has a different audience. That one asks "may THIS USER open a room here?" and
    /// needs a workspace member; this one asks "may ANYONE do billable work in this tenant?" and is
    /// applied to people who are not members at all — an external guest joining by room code has no
    /// membership to check, and a suspended tenant must still not stream their audio through STT
    /// and TTS.
    ///
    /// Fails OPEN on transport failure, unlike ValidateMeetingCreationAsync. The paths that call
    /// this — join and start — had no dependency on WorkspaceService before, and turning a
    /// WorkspaceService outage into "nobody in the product can enter a meeting" is a far worse
    /// outcome than letting an already-suspended tenant finish the call it is in. The bypass is
    /// bounded by the outage and self-corrects; the outage is not bounded by anything.
    /// </summary>
    Task<Result> EnsureWorkspaceCanHostMeetingsAsync(
        Guid workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// WT-342 — the workspace's default answer to "must the host approve people joining?", or
    /// <c>null</c> when the workspace has no usable opinion.
    ///
    /// <c>EnforceHostApprovalDefault</c> has had a working toggle on the workspace settings page,
    /// a field on the DTO, and a value in the settings blob for as long as that page has existed —
    /// and until now NOTHING read it. An admin could turn it on, watch it save, reload the page and
    /// see it on, and every meeting created afterwards ignored it completely.
    ///
    /// Deliberately <c>bool?</c> rather than <c>bool</c>. This is a DEFAULT, and "we could not ask"
    /// has to stay distinguishable from "the workspace said false" — otherwise a WorkspaceService
    /// outage would quietly strip approval from every meeting created during it, which is a
    /// security decision made by a network error. Null means the meeting type's own default stands,
    /// exactly as it did before this method existed.
    ///
    /// Fails OPEN into that null for the same reason
    /// <see cref="EnsureWorkspaceCanHostMeetingsAsync"/> does: room creation carried no dependency
    /// on this before, and turning a WorkspaceService blip into "nobody can create a meeting" is a
    /// far worse outcome than one room falling back to its type's default.
    /// </summary>
    Task<bool?> GetHostApprovalDefaultAsync(
        Guid workspaceId,
        CancellationToken ct = default);
}
