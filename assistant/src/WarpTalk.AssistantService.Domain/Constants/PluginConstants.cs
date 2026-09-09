namespace WarpTalk.AssistantService.Domain.Constants;

public static class PluginConstants
{
    public const int ConfirmationTokenLifetimeMinutes = 5;

    /// <summary>
    /// Who an OAuth grant is with. Several catalog rows can share one provider, so this - not
    /// <c>plugin_key</c> - is what a connection is keyed by and what a compiled-in gateway or OAuth
    /// client asserts on.
    /// </summary>
    /// <remarks>
    /// This replaced a <c>GoogleWorkspace = "google_workspace"</c> constant that meant the plugin
    /// key. 20260907100000 retired that row and split it into google_drive, google_calendar and
    /// google_meet, so nothing may compare a live plugin key against it any more: all three keys
    /// would fail the comparison and every Google tool call would be refused. The constant was not
    /// renamed in place on purpose - a name that still compiles while quietly meaning something
    /// else is the failure mode worth spending a build break to avoid.
    /// </remarks>
    public static class Providers
    {
        /// <summary>
        /// Drive, Calendar and Meet. One consent covers whichever of them the user installs, via
        /// OAuth incremental authorisation.
        /// </summary>
        public const string Google = "google";
    }

    /// <summary>
    /// Plugin keys that a catalog row may not use, because a literal route segment of the same
    /// name sits beside a <c>{pluginKey}</c> route under <c>api/v1/assistant/plugins</c> and
    /// ASP.NET gives the literal precedence. A row with one of these keys is not rejected by
    /// routing - it is silently unreachable, which is the worse failure.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>mcp</c> - the shared MCP OAuth callback, <c>plugins/mcp/oauth/callback</c>. It
    /// shadows <c>plugins/{pluginKey}/oauth/callback</c>, so a row keyed <c>mcp</c> would break the
    /// callback for every MCP plugin at once. Enforced in the database as well, by
    /// <c>plugins_plugin_key_not_reserved</c> since 20260903120000.</item>
    /// <item><c>catalog</c> - the admin surface, <c>plugins/catalog/{pluginKey}</c>. Its literal
    /// first segment outranks the <c>{pluginKey}</c> of <c>plugins/{pluginKey}/connection</c> and
    /// <c>plugins/{pluginKey}/connect-url</c>, so for a row keyed <c>catalog</c> every user-facing
    /// two-segment call would be answered by an admin endpoint - a 403 where a connection status
    /// belongs. Added to the database constraint by 20260907103000.</item>
    /// </list>
    /// <para>
    /// Both names are enforced twice over: here, so an operator gets a sentence rather than a
    /// constraint-violation stack trace, and in <c>plugins_plugin_key_not_reserved</c>, which is
    /// the backstop for every write path that does not come through this code - hand-run SQL
    /// included. Declaring a new literal route reserved means adding it here AND in a migration.
    /// </para>
    /// <para>
    /// The two must agree on case as well as on the list. <see cref="IsReservedPluginKey"/> is
    /// case-insensitive because the router is; the constraint was written exact, which let a
    /// hand-run insert of <c>Catalog</c> through and handed that row's user-facing routes to the
    /// admin controller anyway. 20260909100000 rewrote it as <c>lower(plugin_key) NOT IN (...)</c>.
    /// A name added here has to be added in lower case, and matched the same way at both ends.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> ReservedPluginKeys = ["mcp", "catalog"];

    /// <summary>
    /// True when <paramref name="pluginKey"/> collides with a literal route segment and must not be
    /// stored as a catalog row's key. Case-insensitive: ASP.NET route matching is.
    /// </summary>
    public static bool IsReservedPluginKey(string? pluginKey) =>
        pluginKey is not null
        && ReservedPluginKeys.Contains(pluginKey.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which integration path serves a plugin. This is the dispatch key that lets one catalog hold
    /// both hand-written providers and real MCP servers, so adding an MCP-backed app is an INSERT
    /// rather than a deploy.
    /// </summary>
    public static class PluginKind
    {
        /// <summary>A provider with its own gateway/OAuth implementation compiled in.</summary>
        public const string Native = "native";

        /// <summary>A remote MCP server reached over the protocol; tools come from tools/list.</summary>
        public const string Mcp = "mcp";
    }

    /// <summary>
    /// Which rung of the MCP client-registration ladder a plugin row settled on. MCP Authorization
    /// 2026-07-28 fixes the priority order - pre-registered, then Client ID Metadata Documents,
    /// then Dynamic Client Registration - so a client walks all three rather than picking one.
    /// Persisting the outcome is what keeps the ladder from re-deriving a settled answer.
    /// </summary>
    public static class OAuthClientSource
    {
        /// <summary>Discovery has not run yet; the ladder chooses on first connect.</summary>
        public const string Unresolved = "unresolved";

        /// <summary>An operator supplied the client id (and possibly a secret) at install time.</summary>
        public const string Preregistered = "preregistered";

        /// <summary>The client is identified by our published metadata document URL.</summary>
        public const string Cimd = "cimd";

        /// <summary>Credentials came from RFC 7591 dynamic registration. Deprecated by the spec.</summary>
        public const string Dcr = "dcr";
    }

    /// <summary>
    /// Token-endpoint authentication methods this client can negotiate. Shared-secret methods are
    /// unavailable to a CIMD client - the metadata document is public - so the CIMD path offers
    /// only these two, strongest first.
    /// </summary>
    public static class TokenEndpointAuthMethod
    {
        public const string PrivateKeyJwt = "private_key_jwt";
        public const string None = "none";
        public const string ClientSecretPost = "client_secret_post";
        public const string ClientSecretBasic = "client_secret_basic";
    }

    public static class InstallationStatus
    {
        public const string NotInstalled = "not_installed";
        public const string Installed = "installed";
        public const string Disabled = "disabled";
    }

    public static class ConnectionStatus
    {
        public const string NotConnected = "not_connected";
        public const string Connected = "connected";
        public const string Revoked = "revoked";
        public const string Expired = "expired";
    }

    public static class ToolEffect
    {
        public const string Read = "read";
        public const string Write = "write";
    }

    /// <summary>
    /// Which surface a connect flow started from, carried inside the sealed OAuth state.
    /// </summary>
    /// <remarks>
    /// The desktop app opens the provider's consent page in the system browser, so the browser -
    /// not the app - is holding the result when consent finishes. This is what lets the callback
    /// page know it has to hand the user back through a deep link instead of just rendering.
    /// </remarks>
    public static class OAuthClient
    {
        public const string Web = "web";
        public const string Desktop = "desktop";

        /// <summary>Anything unrecognised is treated as web: the safe direction is to not deep-link.</summary>
        public static string Normalize(string? value) =>
            string.Equals(value, Desktop, StringComparison.Ordinal) ? Desktop : Web;
    }

    /// <summary>
    /// How a connect flow ended, as the callback redirect reports it.
    /// </summary>
    public static class CallbackStatus
    {
        public const string Connected = "connected";

        /// <summary>
        /// The grant is real but narrower than the plugin asked for - the user unticked a scope on
        /// the provider's consent screen. Distinct from <see cref="Error"/> because there is
        /// nothing to retry: the connection works, part of the plugin does not.
        /// </summary>
        public const string Partial = "partial";

        public const string Error = "error";
    }

    public static class ErrorCodes
    {
        public const string PluginNotInstalled = "plugin_not_installed";
        public const string ConnectionRequired = "connection_required";
        public const string MissingScope = "missing_scope";
        public const string ConfirmationRequired = "confirmation_required";
        public const string PermissionDenied = "permission_denied";
        public const string ProviderRateLimited = "provider_rate_limited";
        public const string ProviderUnavailable = "provider_unavailable";
        public const string UnknownPlugin = "unknown_plugin";
        public const string UnknownTool = "unknown_tool";

        /// <summary>
        /// The user pressed Cancel on the provider's consent screen. Not a fault, and kept apart
        /// from <see cref="PermissionDenied"/> for exactly that reason: one is a decision the user
        /// made and should be told about calmly, the other is a state that did not survive and
        /// reads as something going wrong.
        /// </summary>
        public const string AccessDenied = "access_denied";

        /// <summary>
        /// Our own OAuth configuration is missing or wrong - an empty client secret, a client id
        /// the provider does not recognise. Separated from <see cref="ProviderUnavailable"/>
        /// because the two need opposite words: one tells the user to try again later, the other
        /// tells them no amount of retrying will help and an operator has to act.
        /// </summary>
        public const string ProviderConfiguration = "provider_configuration";

        /// <summary>
        /// A catalog edit was rejected before it reached the database: a blank label, a
        /// non-https MCP server URL, an <c>mcp_server_url</c> on a native row. Distinct from
        /// <see cref="InvalidToolManifest"/> so an operator UI can put the message next to the
        /// field it belongs to.
        /// </summary>
        public const string InvalidCatalogUpdate = "invalid_catalog_update";

        /// <summary>
        /// A submitted tool manifest does not satisfy the tool contract. Every such failure is
        /// caught here rather than at chat time, where a malformed manifest shows up only as
        /// WarpBot quietly declining to use a tool.
        /// </summary>
        public const string InvalidToolManifest = "invalid_tool_manifest";

        /// <summary>
        /// A hard delete was refused because installations, connections or recorded tool audits
        /// still reference the row.
        /// </summary>
        /// <remarks>
        /// The three are refused for two different reasons.
        /// <c>plugin_connections_plugin_id_fkey</c> is <c>ON DELETE RESTRICT</c>, so letting the
        /// delete through would surface as a database exception rather than an answer; refusing it
        /// is the same outcome said in words an operator can act on.
        /// <c>plugin_tool_audits_plugin_id_fkey</c> is the opposite - <c>ON DELETE CASCADE</c>, so
        /// the delete would succeed and take the plugin's entire recorded history with it without
        /// saying anything at all. Retiring the row keeps both intact.
        /// </remarks>
        public const string PluginInUse = "plugin_in_use";

        /// <summary>
        /// Every rung of the client-registration ladder was exhausted: the row has no
        /// pre-registered client, and the authorization server advertises neither Client ID
        /// Metadata Document support nor a registration endpoint. Actionable by an operator
        /// (register an app and supply the client id), so it must reach the user as a card
        /// rather than as an exception.
        /// </summary>
        public const string ClientRegistrationUnsupported = "client_registration_unsupported";
    }

    /// <summary>
    /// The workspace plugin-policy refusal. WT-646. Shared because the same refusal has to read
    /// the same whether it surfaces from the catalog, an install, a connect, or a tool call.
    /// </summary>
    public static class WorkspacePolicyMessages
    {
        public const string PluginsDisabled = "Workspace settings do not allow personal plugins in WarpBot.";

        /// <summary>
        /// A call that runs a plugin inside a conversation named no workspace to be judged against.
        /// </summary>
        /// <remarks>
        /// Deliberately a refusal rather than the permissive answer the personal surfaces get. The
        /// plugins settings page has no workspace and must not be gated by one; a tool call always
        /// happens inside a conversation that has one, so an absent workspace there is a malformed
        /// request - and treating it as "no policy to apply" turns the workspace's only plugin
        /// control into a field the caller can delete.
        /// </remarks>
        public const string WorkspaceRequired =
            "This request must name the workspace the plugin is being used in.";

        /// <summary>
        /// The caller named a workspace they do not actively belong to.
        /// </summary>
        /// <remarks>
        /// Without this, naming a workspace is naming a policy: a member of a workspace that has
        /// turned plugins off could send the id of any workspace that has them on - their own
        /// personal one will do - and be judged by that one instead. It also stops a caller from
        /// stamping another workspace's id onto the audit trail.
        /// </remarks>
        public const string NotAWorkspaceMember =
            "You are not an active member of the workspace named in this request.";
    }
}
