using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace WarpTalk.Gateway.Presence;

public static class PresenceEndpoints
{
    /// <summary>
    /// Presence for a set of users, so a page can paint the right dots on first render instead
    /// of waiting for someone's state to happen to change.
    ///
    /// Served from the Gateway because that is where presence lives — it is derived from hub
    /// connections and never persisted, so no downstream service could answer this.
    ///
    /// WT-335: scoped to the caller's own workspaces. This handler's parameters used to be
    /// <c>(request, store, ct)</c> — there was no caller identity in scope at all, so the only
    /// authorization was <c>RequireAuthorization()</c> and any authenticated user could read the
    /// online state of anyone in the system, 500 ids at a time, across every tenant. That is the
    /// invariant <see cref="PresenceNotifier"/> states for the push path ("a global broadcast would
    /// leak who is online across tenants") and the pull path never honoured.
    /// </summary>
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/presence/query", async (
                [FromBody] PresenceQueryRequest request,
                ClaimsPrincipal user,
                IPresenceStore store,
                IPresenceVisibility visibility,
                CancellationToken ct) =>
            {
                var userIds = (request.UserIds ?? [])
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct()
                    .Take(MaxUsersPerQuery)
                    .ToArray();

                var callerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                                   ?? user.FindFirstValue("sub");

                if (string.IsNullOrWhiteSpace(callerUserId))
                {
                    // Authenticated but unidentifiable. Nothing to intersect against, so nothing is
                    // visible — and the response still looks exactly like "everyone is offline".
                    return Results.Ok(AllOffline(userIds));
                }

                var visibleIds = await visibility.FilterVisibleAsync(callerUserId, userIds, ct);

                // Only the visible ids are looked up. Users outside the caller's workspaces are
                // reported Offline — the SAME value a genuinely-offline colleague gets — rather
                // than omitted or flagged. An omission or a "denied" marker would still confirm the
                // account exists, which is the leak one level down: presence is how you probe
                // whether a person is in the system at all.
                var states = await store.GetAsync(visibleIds.ToArray(), ct);

                var response = AllOffline(userIds);
                foreach (var pair in states)
                {
                    response[pair.Key] = pair.Value.ToString();
                }

                return Results.Ok(new PresenceQueryResponse(response));
            })
            .RequireAuthorization()
            .WithName("QueryPresence");

        return app;
    }

    /// <summary>
    /// Every id the caller asked about, answered <see cref="PresenceState.Offline"/>. The response
    /// shape must not depend on whether the caller was allowed to see someone, so this is the
    /// baseline and real states are written over it.
    /// </summary>
    private static Dictionary<string, string> AllOffline(IEnumerable<string> userIds)
    {
        var offline = PresenceState.Offline.ToString();
        var response = new Dictionary<string, string>();

        foreach (var id in userIds)
        {
            response[id] = offline;
        }

        return response;
    }

    /// <summary>
    /// POST rather than GET with a query string: a workspace member list can be long, and ids in
    /// a URL end up in access logs. The cap keeps one request from turning into an unbounded
    /// Redis fan-out.
    /// </summary>
    private const int MaxUsersPerQuery = 500;
}

public sealed record PresenceQueryRequest(string[]? UserIds);

public sealed record PresenceQueryResponse(Dictionary<string, string> States);
