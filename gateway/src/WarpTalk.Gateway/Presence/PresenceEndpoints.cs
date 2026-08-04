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
    /// </summary>
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/presence/query", async (
                [FromBody] PresenceQueryRequest request,
                IPresenceStore store,
                CancellationToken ct) =>
            {
                var userIds = (request.UserIds ?? [])
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct()
                    .Take(MaxUsersPerQuery)
                    .ToArray();

                var states = await store.GetAsync(userIds, ct);

                return Results.Ok(new PresenceQueryResponse(
                    states.ToDictionary(pair => pair.Key, pair => pair.Value.ToString())));
            })
            .RequireAuthorization()
            .WithName("QueryPresence");

        return app;
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
