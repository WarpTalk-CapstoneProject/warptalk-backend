namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>
/// Everything needed to add an MCP-backed app to the catalog.
/// </summary>
/// <remarks>
/// This is the surface that makes "the catalog is data, not code" true in practice. Before it, a
/// new app meant hand-written SQL against a running database, which is neither reviewable nor
/// something anyone can be shown doing.
/// <para>
/// <see cref="OAuth"/> is optional and usually omitted: discovery runs on the first connect and the
/// registration ladder picks CIMD or dynamic registration on its own. It is needed only for a
/// server that supports neither, where an operator registers an app by hand and supplies the client
/// id here.
/// </para>
/// </remarks>
public record CreateMcpPluginRequest(
    string PluginKey,
    string Label,
    string Description,
    string McpServerUrl,
    string? AvatarUrl = null,
    IReadOnlyList<string>? RequiredScopes = null,
    CreateMcpPluginOAuthRequest? OAuth = null);

/// <summary>
/// Pre-registered client credentials, for a server that supports neither Client ID Metadata
/// Documents nor dynamic registration.
/// </summary>
/// <remarks>
/// The endpoints are optional here too: supplying them skips discovery, which is useful against a
/// server whose well-known documents are incomplete. Leave them out and discovery fills them in.
/// </remarks>
public record CreateMcpPluginOAuthRequest(
    string ClientId,
    string? ClientSecret = null,
    string? AuthorizationEndpoint = null,
    string? TokenEndpoint = null,
    string? RevokeEndpoint = null);
