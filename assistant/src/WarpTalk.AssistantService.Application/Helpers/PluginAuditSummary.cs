using System.Globalization;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// The redacted state of a marketplace row, as the platform audit log records it before and after
/// an admin's change.
/// </summary>
/// <remarks>
/// NO SECRETS, and nothing a secret could be read back from. The OAuth client secret is only ever a
/// yes/no here, exactly as on the admin DTOs; the client id is left out too, since the audit store
/// has no DELETE grant and anything that reaches it stays there.
/// </remarks>
public static class PluginAuditSummary
{
    public static IReadOnlyDictionary<string, string?> Of(Plugin plugin) => new Dictionary<string, string?>
    {
        ["plugin_key"] = plugin.PluginKey,
        ["label"] = plugin.Label,
        ["kind"] = plugin.Kind,
        ["is_active"] = plugin.IsActive ? "true" : "false",
        ["is_featured"] = plugin.IsFeatured ? "true" : "false",
        ["sort_order"] = plugin.SortOrder.ToString(CultureInfo.InvariantCulture),
        ["auth_mode"] = PluginConstants.AuthMode.Of(plugin.OAuthClientSource),
        ["oauth_client_source"] = plugin.OAuthClientSource,
        ["has_client_secret"] = string.IsNullOrEmpty(plugin.OAuthClientSecretEncrypted) ? "false" : "true",
        ["mcp_server_url"] = plugin.McpServerUrl,
        ["avatar_url"] = plugin.AvatarUrl,
        ["workspace_default"] = plugin.WorkspaceDefault,
        ["allowed_plan_slugs"] = plugin.AllowedPlanSlugsJson,
    };

    /// <summary>
    /// One workspace's platform state for one plugin, before or after an admin's override change.
    /// <c>override</c> is <c>none</c> when the workspace follows the default.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> OfWorkspace(
        Plugin plugin,
        Guid workspaceId,
        WorkspacePluginOverride? workspaceOverride) => new Dictionary<string, string?>
    {
        ["plugin_key"] = plugin.PluginKey,
        ["workspace_id"] = workspaceId.ToString(),
        ["override"] = workspaceOverride?.State ?? "none",
        ["reason"] = workspaceOverride?.Reason,
    };

    /// <summary>
    /// What an edit amounts to: retiring and reinstating are the two an operator searches the audit
    /// log for, so they get their own verbs rather than hiding inside "updated".
    /// </summary>
    public static string UpdateAction(bool wasActive, bool isActive) => (wasActive, isActive) switch
    {
        (true, false) => WarpTalk.Shared.Events.AdminAuditPluginActions.Retired,
        (false, true) => WarpTalk.Shared.Events.AdminAuditPluginActions.Reinstated,
        _ => WarpTalk.Shared.Events.AdminAuditPluginActions.Updated,
    };
}
