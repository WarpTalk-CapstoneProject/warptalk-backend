using System.Text.Json.Nodes;

namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>
/// <paramref name="Kind"/> selects which integration path serves this plugin
/// (<c>PluginConstants.PluginKind</c>): a compiled-in provider, or a remote MCP server. It is the
/// key <c>IPluginProviderResolver</c> dispatches on, so Application never names a concrete provider.
/// </summary>
public record PluginDefinitionDto(
    Guid Id,
    string Key,
    /// <summary>
    /// Who the OAuth grant is with. Several catalog rows share one provider - google_drive,
    /// google_calendar and google_meet are all <c>google</c> - and a connection is keyed by this,
    /// not by <paramref name="Key"/>. It is also what a compiled-in gateway or OAuth client asserts
    /// on before it sends a user's token anywhere.
    /// </summary>
    string Provider,
    string Label,
    string Description,
    string? AvatarUrl,
    IReadOnlyList<string> RequiredScopes,
    IReadOnlyList<McpToolDescriptorDto> Tools,
    string Kind = "native",
    /// <summary>Null for a native row, which has no MCP server to talk to.</summary>
    string? McpServerUrl = null,
    /// <summary>Operator-curated presentation. See the same members on <see cref="PluginCatalogItemDto"/>.</summary>
    bool IsFeatured = false,
    int SortOrder = 0,
    string? Category = null);

public record PluginCatalogItemDto(
    string Key,
    /// <summary>
    /// Who the OAuth grant is with. Several rows share one provider - google_drive,
    /// google_calendar and google_meet are all <c>google</c> - and one grant serves all of them,
    /// so this is what a client must group by to tell a user that disconnecting one row
    /// disconnects its siblings. Deriving that grouping from scope URLs instead, as the frontend
    /// briefly had to, guesses at something the catalog already knows.
    /// </summary>
    string Provider,
    string Label,
    string Description,
    string? AvatarUrl,
    IReadOnlyList<string> RequiredScopes,
    string InstallationStatus,
    string ConnectionStatus,
    string? ConnectedAccountEmail,
    IReadOnlyList<McpToolDescriptorDto> Tools,
    IReadOnlyList<string> GrantedScopes,
    /// <summary>
    /// Why the active workspace's plugin policy refuses this row, or null when nothing refuses it.
    /// WT-646.
    /// </summary>
    /// <remarks>
    /// Null whenever the catalog was listed without a workspace in context, which is the personal
    /// plugins page's own case and the default.
    /// <para>
    /// A blocked row is REPORTED rather than hidden, and that is the deliberate answer to what
    /// happens when an admin turns plugins off under a user who has already installed and
    /// connected. Hiding it would leave a live OAuth grant the user can neither see nor revoke;
    /// deleting the connection would throw away a personal grant on a workspace's say-so, and the
    /// same grant may be in use in another workspace that still permits it. So the rows stand, the
    /// user can still disconnect, and the plugin is stopped where it would actually be used - at
    /// tool execution.
    /// </para>
    /// </remarks>
    string? WorkspacePolicyBlockReason = null,
    /// <summary>
    /// Operator-curated presentation, set from the admin catalog surface. Exposed here because the
    /// user-facing page is the only place they mean anything: a "Featured" heading rendered over
    /// the whole catalog, which is what this page did before these existed, stops being true the
    /// moment the catalog holds more than a handful of rows.
    /// </summary>
    bool IsFeatured = false,
    /// <summary>Ascending. Ties are the client's to break, by label.</summary>
    int SortOrder = 0,
    /// <summary>Null on every row today; grouping by it is only worth it once rows carry one.</summary>
    string? Category = null);

public record InstallPluginRequest();

public record PluginConnectionStatusDto(
    string PluginKey,
    string Status,
    string? ProviderEmail,
    IReadOnlyList<string> GrantedScopes);

public record PluginConnectUrlDto(string Url);

/// <summary>
/// What has to survive the browser round trip between building an authorization URL and handling
/// the callback.
/// </summary>
/// <remarks>
/// <see cref="CodeVerifier"/> and <see cref="Issuer"/> are null for a <c>native</c> plugin, which
/// runs a provider-specific flow, and populated for <c>kind='mcp'</c>.
/// <para>
/// Carrying the PKCE verifier here rather than in a server-side store is safe because the state is
/// encrypted, not merely opaque - <c>DataProtectionPluginOAuthStateProtector</c> protects it, so a
/// verifier in the URL is unreadable to the user agent it passes through. It also means a callback
/// needs no lookup to be completed, which matters for a redirect that may arrive at a different
/// replica than the one that started the flow.
/// </para>
/// <para>
/// <see cref="Issuer"/> is recorded for RFC 9207: the issuer that came back in the authorization
/// response must be compared against the one discovery validated, and comparing it against
/// anything re-fetched later would defeat the check.
/// </para>
/// </remarks>
public record PluginOAuthStateDto(
    Guid UserId,
    string PluginKey,
    string? CodeVerifier = null,
    string? Issuer = null);

public record PluginOAuthTokenDto(
    string? ProviderAccountId,
    string? ProviderEmail,
    IReadOnlyList<string> GrantedScopes,
    string AccessToken,
    string? RefreshToken,
    DateTime? AccessTokenExpiresAt);

/// <summary>
/// Why a refresh-token exchange ended the way it did, expressed in provider-neutral terms.
/// </summary>
/// <remarks>
/// This is the whole point of the type: the Infrastructure OAuth client owns the provider's status
/// codes and error bodies, and hands Application a decision it can act on without knowing that
/// Google, HTTP, or <c>invalid_grant</c> exist. Only <see cref="GrantRejected"/> is proof the
/// stored grant is dead; every other failure leaves the connection exactly as it was.
/// </remarks>
public enum PluginOAuthRefreshOutcome
{
    /// <summary>A usable access token came back.</summary>
    Succeeded,

    /// <summary>
    /// The provider refused the refresh token itself - revoked grant, changed password, pruned
    /// token. Nothing but a fresh consent fixes it, so the connection ends here.
    /// </summary>
    GrantRejected,

    /// <summary>
    /// The provider or the network got in the way - outage, timeout, DNS, an unclassified
    /// response. The grant is not proven dead; retrying later is the right move.
    /// </summary>
    ProviderUnavailable,

    /// <summary>The provider throttled us. Transient, and worth telling the caller apart from a plain outage.</summary>
    ProviderRateLimited,
}

/// <summary>
/// Outcome of <see cref="Interfaces.IPluginOAuthClient.RefreshAccessTokenAsync"/>.
/// <see cref="Token"/> is non-null exactly when <see cref="Outcome"/> is
/// <see cref="PluginOAuthRefreshOutcome.Succeeded"/>.
/// </summary>
public record PluginOAuthRefreshResultDto(
    PluginOAuthRefreshOutcome Outcome,
    PluginOAuthTokenDto? Token,
    string? Detail = null);

/// <summary>
/// A tool's <see cref="ResourceKey"/> groups it with sibling tools in the catalog UI (for example,
/// a plugin whose OAuth grant covers two distinct products can render one tile per product without
/// the frontend hardcoding provider-specific logic). Null when a plugin's tools are not grouped.
/// </summary>
public record McpToolDescriptorDto(
    string Name,
    string PluginKey,
    string Label,
    string Description,
    string Effect,
    IReadOnlyList<string> RequiredScopes,
    JsonObject Parameters,
    string? ResourceKey = null,
    string? ResourceLabel = null,
    string? ResourceAvatarUrl = null);

public record McpToolExecutionRequest(
    Guid? WorkspaceId,
    string PluginKey,
    string ToolName,
    JsonObject? Arguments,
    Guid? ConversationId,
    Guid? AssistantMessageId,
    string? ConfirmationToken);

public record McpToolExecutionResult(
    bool IsSuccess,
    string? ErrorCode,
    string? Message,
    JsonObject? Data,
    string? ProviderResourceRef,
    string? ConfirmationToken,
    string? PluginKey = null,
    string? PluginLabel = null,
    string? ConnectionStatus = null,
    string? ConnectedAccountEmail = null);
