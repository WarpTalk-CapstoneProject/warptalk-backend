using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace WarpTalk.Gateway.Presence;

public static class PresenceEndpoints
{
    /// <summary>
    /// Presence for a set of users, so a page can paint the right dots on first render instead
    /// of waiting for someone's state to happen to change.
    ///
    /// LEGACY. The web client asks over the notification hub it already holds open
    /// (<c>NotificationHub.QueryPresence</c>); this endpoint stays for clients built before that
    /// and answers from the SAME <see cref="IPresenceQueryService"/>, so the authorization rule and
    /// the cap cannot differ between the two doors.
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
    ///
    /// POST rather than GET with a query string: a workspace member list can be long, and ids in
    /// a URL end up in access logs.
    /// </summary>
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/presence/query", async (
                [FromBody] PresenceQueryRequest request,
                ClaimsPrincipal user,
                IPresenceQueryService presence,
                CancellationToken ct) =>
            {
                var callerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                                   ?? user.FindFirstValue("sub");

                return Results.Ok(await presence.QueryAsync(callerUserId, request.UserIds, ct));
            })
            .RequireAuthorization()
            .WithName("QueryPresence");

        return app;
    }
}

public sealed record PresenceQueryRequest(string[]? UserIds);

public sealed record PresenceQueryResponse(Dictionary<string, string> States);
