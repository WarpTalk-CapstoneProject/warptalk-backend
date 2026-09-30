using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// Which scopes an authorization request for an MCP server asks for. WT-710.
/// </summary>
/// <remarks>
/// MCP Authorization's scope selection strategy is: the <c>scope</c> from the server's
/// <c>WWW-Authenticate</c> challenge first; failing that, every scope in the protected resource's
/// <c>scopes_supported</c>. The authorization server's own list is kept as a last resort for a
/// resource that advertises nothing (see <see cref="McpServerDiscoveryDto"/>).
/// <para>
/// A row's declared <c>required_scopes_json</c> still wins when it has any. Those are what the
/// orchestrator's scope gate checks every tool against, so asking for anything that does not
/// include them would produce a grant the gate then refuses. The challenged scopes are added to
/// them rather than replacing them, because the server's challenge is authoritative for what the
/// request needs.
/// </para>
/// <para>
/// <c>offline_access</c> is added whenever the authorization server lists it: without it most
/// OpenID-style servers never issue a refresh token, and the connection would last one access
/// token's lifetime.
/// </para>
/// </remarks>
public static class McpScopeSelection
{
    public const string OfflineAccess = "offline_access";

    public static IReadOnlyList<string> Select(
        IReadOnlyList<string> requiredScopes,
        McpServerDiscoveryDto? discovery)
    {
        if (discovery is null) return requiredScopes;

        var challenged = discovery.ChallengedScopes ?? Array.Empty<string>();

        IEnumerable<string> chosen;
        if (requiredScopes.Count > 0)
            chosen = requiredScopes.Concat(challenged);
        else if (challenged.Count > 0)
            chosen = challenged;
        else if (discovery.ResourceScopesSupported.Count > 0)
            chosen = discovery.ResourceScopesSupported;
        else
            chosen = discovery.AuthorizationServer.ScopesSupported;

        if (discovery.AuthorizationServer.ScopesSupported.Contains(OfflineAccess, StringComparer.Ordinal))
            chosen = chosen.Append(OfflineAccess);

        return chosen
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
