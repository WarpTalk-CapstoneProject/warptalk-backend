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
        // WT-466: the room's source language. It travels with the targets because the workspace
        // whitelist applies to it identically — and used not to see it at all.
        string? sourceLanguage = null,
        CancellationToken ct = default);

    /// <summary>
    /// WT-466: applies the workspace's allowed-language whitelist to a language set, and NOTHING
    /// else — no host permission, no plan quota, no active-room count.
    ///
    /// It exists because editing a room is not creating one. <c>UpdateTranslationRoomAsync</c>
    /// rewrites SourceLanguage and TargetLanguages after checking only that the platform supports
    /// the code, so a room created inside the policy could be edited straight back out of it and
    /// the owner's setting held for exactly one call. Reusing
    /// <see cref="ValidateMeetingCreationAsync"/> there would have been wrong in a way that is
    /// easy to miss: its active-room check counts the room being edited, so an edit at the room
    /// cap would be denied for a quota the edit does not consume.
    ///
    /// Fails CLOSED, like the creation gate. This is the enforcement path for a rule an owner set
    /// deliberately, and an unreachable WorkspaceService must not become the way around it. The
    /// blast radius is bounded: it refuses an EDIT, never a join and never a live meeting.
    /// </summary>
    Task<Result> ValidateRoomLanguagesAsync(
        Guid workspaceId,
        string? sourceLanguage,
        IEnumerable<string> targetLanguages,
        CancellationToken ct = default);

    /// <summary>
    /// WT-468: the workspace's allowed-language whitelist, for a caller that needs to OFFER the
    /// right choices rather than judge a choice already made.
    /// </summary>
    /// <returns>
    /// The whitelist, or an EMPTY list meaning unrestricted — the same reading every other user of
    /// this list applies. A caller must branch on "no entries" before filtering, or every workspace
    /// that never configured languages ends up offering none.
    /// </returns>
    Task<Result<IReadOnlyList<string>>> GetAllowedLanguagesAsync(
        Guid workspaceId,
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
}
