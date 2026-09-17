using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// The three plugin-request notifications, composed here and sent by <see cref="IUserNotificationClient"/>.
/// </summary>
/// <remarks>
/// EVERY METADATA KEY BELOW IS DECLARED IN THE NOTIFICATION SERVICE'S VALIDATOR. An undeclared key
/// does not get ignored there - it rejects the whole notification with UNSUPPORTED_PAYLOAD_FIELD -
/// so adding a key here without adding it to <c>NotificationValidator.Schemas</c> silently ends
/// delivery. The keys are the same for all three types on purpose, so there is one list to keep in
/// step.
/// </remarks>
public static class PluginRequestNotifications
{
    public static class MetadataKeys
    {
        public const string WorkspaceId = "workspace_id";
        public const string WorkspaceName = "workspace_name";
        public const string PluginKey = "plugin_key";
        public const string PluginLabel = "plugin_label";
        public const string RequestId = "request_id";
    }

    /// <summary>To the workspace Owner, who decides it on the workspace Plugins page.</summary>
    public static UserNotification Requested(
        WorkspaceProfile workspace,
        Guid ownerUserId,
        PluginRequest request,
        Plugin plugin,
        string? requesterEmail)
    {
        var who = string.IsNullOrWhiteSpace(requesterEmail) ? "A member" : requesterEmail.Trim();
        var body = string.IsNullOrWhiteSpace(request.Reason)
            ? $"{who} asked you to add {plugin.Label} to {workspace.Name}."
            : $"{who} asked you to add {plugin.Label} to {workspace.Name}: \"{Plain(request.Reason)}\"";

        return new UserNotification(
            ownerUserId,
            WorkspacePluginConstants.NotificationTypes.Requested,
            $"{who} asked for {plugin.Label}",
            body,
            WorkspacePluginsUrl(workspace),
            Metadata(workspace, request, plugin));
    }

    /// <summary>To the member who asked.</summary>
    public static UserNotification Decided(WorkspaceProfile workspace, PluginRequest request, Plugin plugin)
    {
        var approved = request.Status == WorkspacePluginConstants.RequestStatus.Approved;
        return new UserNotification(
            request.RequestedBy,
            approved
                ? WorkspacePluginConstants.NotificationTypes.RequestApproved
                : WorkspacePluginConstants.NotificationTypes.RequestDeclined,
            approved
                ? $"{plugin.Label} was added to {workspace.Name}"
                : $"{plugin.Label} was not added to {workspace.Name}",
            approved
                ? $"Your workspace owner added {plugin.Label}. You can connect it from Plugins."
                : $"Your workspace owner decided not to add {plugin.Label} for now.",
            // The member's own plugins page, which is where Connect lives.
            "/settings/plugins",
            Metadata(workspace, request, plugin));
    }

    /// <summary>
    /// The notification service refuses a title or body that looks like HTML, and refuses the whole
    /// notification rather than the tag. A member's reason is free text, so its angle brackets go.
    /// </summary>
    private static string Plain(string text) =>
        text.Trim().Replace("<", string.Empty, StringComparison.Ordinal).Replace(">", string.Empty, StringComparison.Ordinal);

    public static string WorkspacePluginsUrl(WorkspaceProfile workspace) =>
        string.IsNullOrWhiteSpace(workspace.Slug) ? "/settings/plugins" : $"/{workspace.Slug}/settings/plugins";

    private static IReadOnlyDictionary<string, string> Metadata(
        WorkspaceProfile workspace,
        PluginRequest request,
        Plugin plugin) =>
        new Dictionary<string, string>
        {
            [MetadataKeys.WorkspaceId] = workspace.WorkspaceId.ToString(),
            [MetadataKeys.WorkspaceName] = workspace.Name,
            [MetadataKeys.PluginKey] = plugin.PluginKey,
            [MetadataKeys.PluginLabel] = plugin.Label,
            [MetadataKeys.RequestId] = request.Id.ToString(),
        };
}
