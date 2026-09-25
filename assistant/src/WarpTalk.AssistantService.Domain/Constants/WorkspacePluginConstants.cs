namespace WarpTalk.AssistantService.Domain.Constants;

/// <summary>
/// The workspace half of the plugin marketplace: which plugins a workspace has, and members asking
/// for more.
/// </summary>
/// <remarks>
/// Its own file rather than more nested classes in <see cref="PluginConstants"/>, which is the file
/// every plugin change touches.
/// </remarks>
public static class WorkspacePluginConstants
{
    /// <summary>Whether a plugin can be used in the workspace a row was listed for.</summary>
    public static class Availability
    {
        /// <summary>A marketplace plugin the workspace has.</summary>
        public const string Added = "added";

        /// <summary>A private MCP plugin this workspace's Owner created.</summary>
        public const string Private = "private";

        /// <summary>A marketplace plugin the workspace does not have; a member may request it.</summary>
        public const string NotAdded = "not_added";

        /// <summary>
        /// A marketplace plugin the PLATFORM has turned off for this workspace (retired, an override,
        /// the plan rule or an opt-in default). Not in the Owner's marketplace, not requestable, not
        /// usable - whatever the workspace's own list says. Members' connections are kept, inert.
        /// </summary>
        public const string DisabledByPlatform = "platform_disabled";

        public static bool IsUsable(string availability) =>
            availability is Added or Private;
    }

    public static class RequestStatus
    {
        public const string Pending = "pending";
        public const string Approved = "approved";
        public const string Declined = "declined";
    }

    /// <summary>Matches <c>plugin_requests.reason VARCHAR(500)</c>.</summary>
    public const int MaxRequestReasonLength = 500;

    /// <summary>Prefix of a private plugin's generated key, so one is recognisable in logs and audits.</summary>
    public const string PrivatePluginKeyPrefix = "ws_";

    public static class ErrorCodes
    {
        public const string RequestAlreadyPending = "plugin_request_already_pending";
        public const string RequestNotPending = "plugin_request_not_pending";

        /// <summary>The workspace Owner asked themselves for a plugin; they add it instead. 409.</summary>
        public const string RequestByOwner = "plugin_request_by_owner";
        public const string UnknownRequest = "unknown_plugin_request";
        public const string InvalidRequest = "invalid_plugin_request";
        public const string PluginAlreadyAvailable = "plugin_already_available";
        public const string PluginRetired = "plugin_retired";
        public const string NotAPrivatePlugin = "not_a_private_plugin";
        public const string InvalidPrivatePlugin = "invalid_private_plugin";
        /// <summary>
        /// Another write to the same list landed first - two first edits both creating the curation
        /// row, or two adds of one plugin - and a unique key refused this one. 409: refetch.
        /// </summary>
        public const string ListChangedConcurrently = "workspace_plugin_list_changed";

        /// <summary>
        /// The workspace service could not say whether AllowAnyPlugins was on, and the change would
        /// have written the workspace's list for the first time from that answer. 503: nothing was
        /// changed, and trying again later is the whole remedy.
        /// </summary>
        public const string PolicyUnavailable = "workspace_plugin_policy_unavailable";

        /// <summary>
        /// The workspace service could not list the workspace's members, so the page cannot be told
        /// which of them connected a plugin without guessing. 503: try again.
        /// </summary>
        public const string MembersUnavailable = "workspace_members_unavailable";
    }

    public static class Messages
    {
        public const string NotAdded = "This plugin has not been added to this workspace. Ask your workspace owner to add it.";
        public const string OwnerOnly = "Only the workspace owner can change which plugins this workspace has.";
        public const string OwnerOrAdminOnly = "Only a workspace owner or admin can see this workspace's plugins and requests.";
        public const string PrivatePluginNeedsItsWorkspace = "This plugin belongs to a workspace. Use it from that workspace.";
        public const string PolicyUnavailable = "Couldn't read this workspace's plugin settings right now, so nothing was changed. Try again in a moment.";
        public const string MembersUnavailable = "Couldn't read this workspace's members right now. Try again in a moment.";
        public const string ListChangedConcurrently = "This workspace's plugins changed while you were editing them. Refresh and try again.";
        public const string OwnerAddsDirectly = "You own this workspace, so there's no one to ask. Add the plugin from the workspace's Plugins settings instead.";
    }

    /// <summary>
    /// Notification types. A NEW TYPE IS TWO EDITS IN TWO SERVICES: each string here must also be in
    /// the notification service's <c>NotificationValidator.Schemas</c>, or it is rejected there with
    /// UNSUPPORTED_NOTIFICATION_TYPE while this service logs it as sent.
    /// </summary>
    public static class NotificationTypes
    {
        /// <summary>To the workspace Owner: a member asked for a plugin.</summary>
        public const string Requested = "PLUGIN_REQUESTED";

        /// <summary>To the member: the Owner added the plugin they asked for.</summary>
        public const string RequestApproved = "PLUGIN_REQUEST_APPROVED";

        /// <summary>To the member: the Owner declined.</summary>
        public const string RequestDeclined = "PLUGIN_REQUEST_DECLINED";
    }
}
