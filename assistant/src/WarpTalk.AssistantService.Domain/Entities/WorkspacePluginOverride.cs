namespace WarpTalk.AssistantService.Domain.Entities;

/// <summary>
/// A platform admin's decision about one marketplace plugin in one workspace, beating the plugin's
/// default and its plan rule. No row: the workspace follows the plugin's default.
/// </summary>
public class WorkspacePluginOverride
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PluginId { get; set; }

    /// <summary><c>enabled</c> or <c>disabled</c>.</summary>
    public string State { get; set; } = null!;

    public string? Reason { get; set; }

    public Guid? SetBy { get; set; }

    public DateTime SetAt { get; set; }
}
