namespace WarpTalk.AssistantService.Domain.Constants;

/// <summary>
/// The platform admin's layer over the marketplace: which workspaces a marketplace plugin may reach.
/// 20260925120000_plugin_workspace_availability.sql.
/// </summary>
/// <remarks>
/// Its own file rather than more nested classes in <see cref="WorkspacePluginConstants"/>, which is
/// the Owner's layer. The two are separate decisions made by different people: the platform decides
/// what an Owner may add, the Owner decides what the workspace has.
/// </remarks>
public static class PluginWorkspaceAccessConstants
{
    /// <summary>
    /// The global default, as the admin picks it. <see cref="Retired"/> is not stored in
    /// <c>workspace_default</c>: it is <c>is_active = false</c>, which already retires a row
    /// everywhere.
    /// </summary>
    public static class Default
    {
        public const string Available = "available";
        public const string OptIn = "opt_in";
        public const string Retired = "retired";

        /// <summary>The two values <c>plugins.workspace_default</c> can hold.</summary>
        public static bool IsStored(string? value) => value is Available or OptIn;

        public static bool IsKnown(string? value) => value is Available or OptIn or Retired;
    }

    /// <summary><c>workspace_plugin_overrides.state</c>.</summary>
    public static class OverrideState
    {
        public const string Enabled = "enabled";
        public const string Disabled = "disabled";
    }

    /// <summary>What an admin asks for on one or many workspaces.</summary>
    public static class OverrideAction
    {
        public const string Enable = "enable";
        public const string Disable = "disable";
        /// <summary>Remove the override: the workspace follows the default and plan rule again.</summary>
        public const string Reset = "reset";

        public static bool IsKnown(string? value) => value is Enable or Disable or Reset;
    }

    /// <summary>Which layer decided a workspace's effective state.</summary>
    public static class Source
    {
        /// <summary>The plugin is retired. Nothing overrides it.</summary>
        public const string Retired = "retired";

        /// <summary>A platform admin's override for this workspace.</summary>
        public const string Override = "override";

        /// <summary>The plan rule excluded this workspace's plan (or it has none).</summary>
        public const string Plan = "plan";

        /// <summary>The plugin's default: available, or opt-in.</summary>
        public const string Default = "default";

        /// <summary>A private plugin: the platform does not govern it.</summary>
        public const string Private = "private";
    }

    public const int MaxReasonLength = 500;

    /// <summary>A bulk apply touches at most this many workspaces in one request.</summary>
    public const int MaxBulkWorkspaces = 500;

    public static class ErrorCodes
    {
        public const string InvalidAvailability = "invalid_plugin_availability";
        public const string InvalidOverride = "invalid_plugin_override";
        public const string UnknownWorkspace = "unknown_workspace";

        /// <summary>
        /// The workspace service did not answer, so the workspace list (and each one's plan) is
        /// unknown. 503: nothing was changed.
        /// </summary>
        public const string WorkspacesUnavailable = "workspaces_unavailable";

        /// <summary>
        /// An Owner tried to add, or a member to request, a plugin the platform has turned off for
        /// this workspace. 409.
        /// </summary>
        public const string DisabledByPlatform = "plugin_disabled_by_platform";
    }

    public static class Messages
    {
        public const string DisabledByPlatform =
            "WarpTalk has turned this plugin off for this workspace. Existing connections are kept, but WarpBot won't use them here.";
        public const string WorkspacesUnavailable =
            "Couldn't read the workspace list right now, so nothing was changed. Try again in a moment.";
    }
}
