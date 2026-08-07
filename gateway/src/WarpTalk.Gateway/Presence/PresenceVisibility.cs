using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.Presence;

/// <summary>
/// WT-335 — "whose presence is this caller allowed to see?"
///
/// <see cref="PresenceNotifier"/> documents the invariant for the PUSH path: presence is fanned out
/// per workspace because "a global broadcast would leak who is online across tenants". The PULL
/// path (<c>POST /api/v1/presence/query</c>) never honoured it — it took a list of user ids and
/// answered for all of them, so any authenticated user could probe the online state of anyone in
/// the system, in any tenant, 500 at a time.
///
/// The obvious cheap source — <see cref="IPresenceStore.GetTrackedWorkspacesAsync"/>, which the
/// push path already uses — is deliberately NOT used here. That set is populated by
/// <c>NotificationHub.SubscribeWorkspace</c>, which does not verify membership either, so it
/// records the workspaces a client CLAIMED to be watching. Authorizing a read off a self-asserted
/// set would let a caller widen their own visibility by subscribing to a workspace id they guessed.
/// The answer has to come from WorkspaceService, which owns membership.
/// </summary>
public interface IPresenceVisibility
{
    /// <summary>
    /// Filters <paramref name="userIds"/> down to those the caller shares an active workspace with.
    /// The caller's own id is always retained — a user may always see themselves, and asking
    /// WorkspaceService whether you share a workspace with yourself is a round trip for a known
    /// answer.
    /// </summary>
    Task<IReadOnlySet<string>> FilterVisibleAsync(
        string callerUserId,
        IReadOnlyCollection<string> userIds,
        CancellationToken ct = default);
}

/// <summary>
/// One gRPC hop per presence query, not one per user id.
///
/// Cost was the constraint that shaped this. The endpoint takes up to 500 ids and sits on a hot
/// path, so the natural-looking implementation — ask <c>GetWorkspaceMemberDetails</c> whether the
/// caller and candidate share a workspace, per candidate — would have turned one request into 500
/// gRPC calls and 500 queries. Instead the whole candidate set goes over in ONE call and
/// WorkspaceService answers it with ONE indexed self-join on <c>workspace_members</c>. The work per
/// additional id is a row in a request message and a row in a join, not a round trip.
///
/// No caching, deliberately. A visibility answer cached for even a minute keeps showing a removed
/// member their old colleagues' presence, which is the same leak with a delay; and the hop it would
/// save is already amortised across up to 500 ids. This follows <see cref="Services.RoomHostAuthority"/>,
/// which refuses to cache host identity for the same class of reason.
///
/// Fails CLOSED. If WorkspaceService cannot be reached we do not know that the caller may see
/// anyone, so nobody is visible and the endpoint reports everyone as Offline. That degrades the
/// presence dots during a WorkspaceService outage, which is the correct trade for a privacy filter:
/// the alternative — falling open to the unfiltered list — restores the exact defect being fixed,
/// and does so precisely when the system is least healthy.
/// </summary>
public sealed class PresenceVisibility : IPresenceVisibility
{
    private readonly WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly ILogger<PresenceVisibility> _logger;

    public PresenceVisibility(
        WorkspaceService.WorkspaceServiceClient workspaceClient,
        ILogger<PresenceVisibility> logger)
    {
        _workspaceClient = workspaceClient;
        _logger = logger;
    }

    public async Task<IReadOnlySet<string>> FilterVisibleAsync(
        string callerUserId,
        IReadOnlyCollection<string> userIds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(callerUserId) || userIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Self is always visible, and is excluded from the lookup rather than answered by it.
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>(userIds.Count);

        foreach (var id in userIds)
        {
            if (string.Equals(id, callerUserId, StringComparison.OrdinalIgnoreCase))
            {
                visible.Add(id);
            }
            else
            {
                candidates.Add(id);
            }
        }

        if (candidates.Count == 0)
        {
            return visible;
        }

        var request = new GetSharedWorkspaceMembersRequest { CallerUserId = callerUserId };
        request.CandidateUserIds.AddRange(candidates);

        try
        {
            var response = await _workspaceClient.GetSharedWorkspaceMembersAsync(
                request, cancellationToken: ct);

            foreach (var id in response.VisibleUserIds)
            {
                visible.Add(id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "PresenceVisibility: could not resolve shared workspaces for {CallerUserId}; reporting {Count} queried users as not visible.",
                callerUserId,
                candidates.Count);
        }

        return visible;
    }
}
