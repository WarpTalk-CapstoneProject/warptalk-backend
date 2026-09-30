namespace WarpTalk.Gateway.Presence;

/// <summary>
/// "What is the presence of these users, as this caller is allowed to see it?" — the one answer
/// both the hub (<c>NotificationHub.QueryPresence</c>, what the web client uses) and the legacy
/// REST endpoint (<c>POST /api/v1/presence/query</c>, kept for older clients) serve.
///
/// One implementation on purpose. The authorization rule (WT-335: only users who share a
/// workspace with the caller), the cap, and the "everyone you may not see is Offline" response
/// shape are the privacy contract; two copies would drift, and the drifted copy is the leak.
///
/// Replica-safe by construction: it reads presence from Redis (<see cref="IPresenceStore"/>) and
/// membership from WorkspaceService (<see cref="IPresenceVisibility"/>), never from a gateway
/// pod's in-memory connection table, so every replica gives the same answer.
/// </summary>
public interface IPresenceQueryService
{
    Task<PresenceQueryResponse> QueryAsync(
        string? callerUserId,
        IEnumerable<string?>? userIds,
        CancellationToken ct = default);
}

public sealed class PresenceQueryService : IPresenceQueryService
{
    /// <summary>
    /// A member list can be long, but one call must not turn into an unbounded Redis fan-out.
    /// Ids beyond the cap are dropped, not answered — the caller batches.
    /// </summary>
    public const int MaxUsersPerQuery = 500;

    private readonly IPresenceStore _store;
    private readonly IPresenceVisibility _visibility;

    public PresenceQueryService(IPresenceStore store, IPresenceVisibility visibility)
    {
        _store = store;
        _visibility = visibility;
    }

    public async Task<PresenceQueryResponse> QueryAsync(
        string? callerUserId,
        IEnumerable<string?>? userIds,
        CancellationToken ct = default)
    {
        var requested = (userIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxUsersPerQuery)
            .ToArray();

        if (string.IsNullOrWhiteSpace(callerUserId))
        {
            // Authenticated but unidentifiable. Nothing to intersect against, so nothing is
            // visible — and the response still looks exactly like "everyone is offline".
            return new PresenceQueryResponse(AllOffline(requested));
        }

        if (requested.Length == 0)
        {
            return new PresenceQueryResponse(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var visibleIds = await _visibility.FilterVisibleAsync(callerUserId, requested, ct);

        // Only the visible ids are looked up. Users outside the caller's workspaces are reported
        // Offline — the SAME value a genuinely-offline colleague gets — rather than omitted or
        // flagged. An omission or a "denied" marker would still confirm the account exists, which
        // is the leak one level down: presence is how you probe whether a person is in the system.
        var states = visibleIds.Count == 0
            ? new Dictionary<string, PresenceState>()
            : await _store.GetAsync(visibleIds.ToArray(), ct);

        var response = AllOffline(requested);
        foreach (var pair in states)
        {
            // Never write a key the caller did not ask for: the response is keyed by the ids as
            // the caller spelled them (visibility matches case-insensitively and may hand back
            // WorkspaceService's spelling), and it must not grow by what the store returns.
            if (response.ContainsKey(pair.Key))
            {
                response[pair.Key] = pair.Value.ToString();
            }
        }

        return new PresenceQueryResponse(response);
    }

    /// <summary>
    /// Every id the caller asked about, answered <see cref="PresenceState.Offline"/>. The response
    /// shape must not depend on whether the caller was allowed to see someone, so this is the
    /// baseline and real states are written over it.
    /// </summary>
    private static Dictionary<string, string> AllOffline(IEnumerable<string> userIds)
    {
        var offline = PresenceState.Offline.ToString();
        var response = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in userIds)
        {
            response[id] = offline;
        }

        return response;
    }
}
