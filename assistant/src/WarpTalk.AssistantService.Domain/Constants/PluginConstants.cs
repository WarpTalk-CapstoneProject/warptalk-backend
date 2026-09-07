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
        /// The workspace runs an allowlist and this plugin key is not on it. WT-646.
        /// </summary>
        /// <remarks>
        /// Deliberately not folded into <see cref="PermissionDenied"/>, which stays the code for
        /// the workspace-wide <c>allow_any_plugins</c> switch. The two refusals need different
        /// words in front of the user - "your workspace has turned personal plugins off" versus
        /// "your workspace permits some plugins but not this one" - and only one of them is
        /// fixable by asking an admin to add a single key.
        /// </remarks>
        public const string WorkspacePluginNotAllowed = "workspace_plugin_not_allowed";

        /// <summary>
        /// The workspace confines plugin installation to Owner and Admin, and the caller is
        /// neither. WT-646.
        /// </summary>
        public const string WorkspaceInstallRequiresAdmin = "workspace_install_requires_admin";

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
    /// Messages for the workspace plugin-policy refusals. WT-646. Shared because the same refusal
    /// has to read the same whether it surfaces from the catalog, an install, a connect, or a tool
    /// call.
    /// </summary>
    public static class WorkspacePolicyMessages
    {
        public const string PluginsDisabled = "Workspace settings do not allow personal plugins in WarpBot.";
        public const string NotOnAllowlist = "This workspace's plugin policy does not include this plugin.";
        public const string InstallRequiresAdmin = "Only a workspace Owner or Admin may install plugins in this workspace.";
    }
}
