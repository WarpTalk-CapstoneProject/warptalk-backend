using System.Text.Json.Nodes;

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

// ---------------------------------------------------------------------------------------------
// The rest of a catalog row's lifecycle (WT-646).
//
// Creating a row was already possible without a deploy; nothing else about it was. There was no way
// to list, edit, re-credential, re-tool or retire a row, so a wrong client id in production meant
// hand-written SQL and adding one tool meant a migration. These types back the missing half.
//
// One rule shapes every type below: a client secret never leaves the service. Not to an operator,
// not masked, not truncated. The only facts exposed about it are that one exists and, to the
// precision the schema allows, when the row was last written.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One catalog row as the admin listing shows it - including rows retired with
/// <c>is_active = false</c>, which is exactly the set the user-facing catalog hides and the set an
/// operator most needs to see.
/// </summary>
/// <remarks>
/// <paramref name="HasClientId"/> and <paramref name="HasClientSecret"/> are booleans on purpose.
/// An operator checking whether a row is credentialed needs a yes/no, and a yes/no is the largest
/// answer that cannot leak a secret into a log, a screenshot or a browser cache.
/// </remarks>
public record PluginCatalogAdminListItemDto(
    string PluginKey,
    string Label,
    string Description,
    string Kind,
    string Provider,
    bool IsActive,
    bool IsFeatured,
    int SortOrder,
    string? Category,
    string OAuthClientSource,
    bool HasClientId,
    bool HasClientSecret,
    int ToolCount,
    int InstallationCount);

/// <summary>
/// One catalog row in full, with the tool manifest that <c>PUT .../tools</c> replaces.
/// </summary>
/// <remarks>
/// <paramref name="OAuthClientId"/> is present and no secret is, and the asymmetry is deliberate
/// rather than an oversight: a client id is public by construction - it travels in every
/// authorization URL the user's own browser follows - and an operator rotating credentials has to
/// be able to confirm which id the row now holds. A secret has no such justification, so the row
/// answers only <paramref name="HasClientSecret"/>.
/// <para>
/// <paramref name="CredentialsUpdatedAt"/> is the row's own <c>updated_at</c>, not a dedicated
/// "secret last set" column - the catalog has none. Any edit bumps it, so it is an upper bound on
/// when the secret was written rather than the exact moment, and it is named for what it is instead
/// of implying a precision the schema cannot back.
/// </para>
/// </remarks>
public record PluginCatalogAdminDetailDto(
    string PluginKey,
    string Label,
    string Description,
    string? AvatarUrl,
    string Kind,
    string Provider,
    string? McpServerUrl,
    IReadOnlyList<string> RequiredScopes,
    bool IsActive,
    bool IsFeatured,
    int SortOrder,
    string? Category,
    string OAuthClientSource,
    string? OAuthClientId,
    bool HasClientId,
    bool HasClientSecret,
    DateTime? CredentialsUpdatedAt,
    string? OAuthAuthorizationEndpoint,
    string? OAuthTokenEndpoint,
    string? OAuthRevokeEndpoint,
    string? OAuthRegistrationEndpoint,
    string? OAuthTokenEndpointAuthMethod,
    DateTime? ToolsSyncedAt,
    IReadOnlyList<McpToolDescriptorDto> Tools,
    int InstallationCount,
    int ConnectionCount,
    Guid? UpdatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// A partial edit: every property is optional and only the ones supplied are written.
/// </summary>
/// <remarks>
/// <c>null</c> means "not supplied", which is the only reading a JSON body gives a missing property
/// without a tri-state wrapper on every field. The two columns that are genuinely nullable -
/// <paramref name="AvatarUrl"/> and <paramref name="Category"/> - are therefore cleared by sending
/// an empty string, not by sending null. That is stated here because it is the kind of contract an
/// operator otherwise discovers the hard way.
/// </remarks>
public record UpdatePluginCatalogRequest(
    string? Label = null,
    string? Description = null,
    string? AvatarUrl = null,
    string? McpServerUrl = null,
    IReadOnlyList<string>? RequiredScopes = null,
    bool? IsActive = null,
    bool? IsFeatured = null,
    int? SortOrder = null,
    string? Category = null);

/// <summary>
/// Sets or rotates a row's pre-registered OAuth client.
/// </summary>
/// <remarks>
/// <paramref name="ClientSecret"/> is tri-state by convention: omitted (null) leaves whatever secret
/// the row holds alone, an empty string clears it, and a value replaces it. Rotating an id without
/// re-sending the secret is the common case and must not silently wipe the secret - and since no
/// endpoint ever hands the secret back, an operator could not re-send it even if asked to.
/// </remarks>
public record SetPluginOAuthClientRequest(
    string? ClientId,
    string? ClientSecret = null,
    string? AuthorizationEndpoint = null,
    string? TokenEndpoint = null,
    string? RevokeEndpoint = null);

/// <summary>
/// Replaces <c>tools_json</c> wholesale.
/// </summary>
/// <remarks>
/// Wholesale rather than incremental because the manifest is a contract WarpBot reads as a unit: a
/// per-tool patch API would let two operators interleave edits into a manifest neither of them
/// intended. An empty list is accepted and means "this row advertises no tools", which is the
/// correct state for a fresh <c>kind='mcp'</c> row whose tools arrive from <c>tools/list</c>.
/// </remarks>
public record ReplacePluginToolsRequest(IReadOnlyList<PluginToolManifestEntryDto>? Tools);

/// <summary>
/// One tool in a submitted manifest.
/// </summary>
/// <remarks>
/// This mirrors <see cref="McpToolDescriptorDto"/> minus <c>pluginKey</c>, which the service fills
/// in from the route rather than trusting the body. The orchestrator resolves a tool call by that
/// key, so a manifest naming another row's key would route calls at the wrong plugin - a failure
/// that surfaces at chat time, nowhere near this endpoint.
/// <para>
/// Every property is nullable so that a malformed body reaches the validator as missing data and
/// comes back as a readable error, instead of failing inside the JSON deserializer as a 400 with
/// nothing actionable in it.
/// </para>
/// <para>
/// <c>resourceKey</c>, <c>resourceLabel</c> and <c>resourceAvatarUrl</c> are not accepted. They
/// grouped a single plugin's tools by product before 20260907100000 split google_workspace into
/// three rows and made the plugin row itself the grouping; a body that still sends them has them
/// ignored, which is the same answer the migration gave the rows it moved.
/// </para>
/// </remarks>
public record PluginToolManifestEntryDto(
    string? Name,
    string? Label,
    string? Description,
    string? Effect,
    IReadOnlyList<string>? RequiredScopes,
    JsonObject? Parameters);

/// <summary>
/// What a delete actually did, so the caller need not infer it from the status code.
/// </summary>
/// <remarks>
/// The four counts are everything that references the row, reported whichever way the delete went.
/// They are what makes the difference between the two outcomes legible: a retired row still has all
/// of them, and a hard delete is only offered when the first three are zero. WT-646.
/// <para>
/// <paramref name="AuditCount"/> and <paramref name="ConfirmationTokenCount"/> are here because
/// their foreign keys are <c>ON DELETE CASCADE</c> - unlike installations and connections, they do
/// not block a delete, they disappear into it. Reporting them is how an operator finds out that a
/// row they retired was carrying two years of tool history.
/// </para>
/// </remarks>
public record PluginCatalogDeleteResultDto(
    string PluginKey,
    bool HardDeleted,
    int InstallationCount,
    int ConnectionCount,
    int AuditCount,
    int ConfirmationTokenCount);

/// <summary>Filters and paging for the tool-audit listing.</summary>
/// <remarks>
/// <paramref name="Outcome"/> matches <c>plugin_tool_audits.result_status</c>, which holds either
/// <c>"ok"</c> or one of <c>PluginConstants.ErrorCodes</c> - the recorder writes the error code
/// itself as the status, so filtering by outcome is how an operator finds every call that failed
/// for one reason.
/// </remarks>
public record PluginToolAuditQueryDto(
    Guid? UserId = null,
    string? Outcome = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>One recorded tool invocation.</summary>
/// <remarks>
/// <c>input_summary</c> is the leading 500 characters of the tool arguments, so it can hold whatever
/// a user typed into a chat. It is carried here because reconstructing what an operator is looking
/// at without it is guesswork, and this endpoint is gated on the system-admin policy.
/// </remarks>
public record PluginToolAuditEntryDto(
    Guid Id,
    Guid? WorkspaceId,
    Guid UserId,
    Guid? ConversationId,
    Guid? AssistantMessageId,
    string PluginKey,
    string ToolName,
    string? InputSummary,
    string ResultStatus,
    string? ProviderResourceRef,
    DateTime CreatedAt);

/// <summary>A page of <see cref="PluginToolAuditEntryDto"/>, newest first.</summary>
public record PluginToolAuditPageDto(
    IReadOnlyList<PluginToolAuditEntryDto> Items,
    int Page,
    int PageSize,
    int TotalCount);
