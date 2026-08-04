namespace WarpTalk.Gateway.Presence;

/// <summary>
/// Live presence for workspace members, kept in Redis rather than in process memory.
///
/// Redis rather than <see cref="Hubs.IConnectionManager"/> for two reasons: the Members page has
/// to render who is online on first paint, which means presence must be readable outside the
/// socket that produced it; and a second Gateway instance must not report its own half of the
/// world as the whole of it.
///
/// Every record carries a TTL that the owning connection refreshes. Nothing here relies on a
/// clean shutdown to stay correct.
/// </summary>
public interface IPresenceStore
{
    /// <summary>
    /// Records a user as reachable. Does not downgrade someone already in a meeting — a second
    /// tab connecting must not drag them out of the meeting state.
    /// </summary>
    Task SetOnlineAsync(string userId, CancellationToken ct = default);

    /// <summary>Marks the user as being in a live meeting.</summary>
    Task SetInMeetingAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Drops the meeting state back to plain online. A no-op when the user has already gone
    /// fully offline, so a late leave-event cannot resurrect them.
    /// </summary>
    Task ClearInMeetingAsync(string userId, CancellationToken ct = default);

    /// <summary>Removes the user's presence entirely — called when the last connection closes.</summary>
    Task SetOfflineAsync(string userId, CancellationToken ct = default);

    /// <summary>Extends the TTL on an existing record without changing its state.</summary>
    Task RefreshAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Current state for each requested user. Users with no record come back as
    /// <see cref="PresenceState.Offline"/>, so callers always get an entry per id they asked for.
    /// </summary>
    Task<IReadOnlyDictionary<string, PresenceState>> GetAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Remembers that this user is watching a workspace, so a later state change knows which
    /// groups to announce itself to. Presence is only interesting to people who share a
    /// workspace with you.
    /// </summary>
    Task TrackWorkspaceAsync(string userId, string workspaceId, CancellationToken ct = default);

    /// <summary>Workspaces this user is currently watching.</summary>
    Task<IReadOnlyCollection<string>> GetTrackedWorkspacesAsync(
        string userId,
        CancellationToken ct = default);
}
