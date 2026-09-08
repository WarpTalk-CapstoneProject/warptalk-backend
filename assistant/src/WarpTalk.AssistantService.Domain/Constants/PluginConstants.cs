namespace WarpTalk.AssistantService.Domain.Constants;

public static class PluginConstants
{
    public const string GoogleWorkspace = "google_workspace";
    public const int ConfirmationTokenLifetimeMinutes = 5;

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
        /// Our own OAuth configuration is missing or wrong - an empty client secret, a client id
        /// the provider does not recognise. Separated from <see cref="ProviderUnavailable"/>
        /// because the two need opposite words: one tells the user to try again later, the other
        /// tells them no amount of retrying will help and an operator has to act.
        /// </summary>
        public const string ProviderConfiguration = "provider_configuration";

        /// <summary>
        /// Every rung of the client-registration ladder was exhausted: the row has no
        /// pre-registered client, and the authorization server advertises neither Client ID
        /// Metadata Document support nor a registration endpoint. Actionable by an operator
        /// (register an app and supply the client id), so it must reach the user as a card
        /// rather than as an exception.
        /// </summary>
        public const string ClientRegistrationUnsupported = "client_registration_unsupported";
    }
}
