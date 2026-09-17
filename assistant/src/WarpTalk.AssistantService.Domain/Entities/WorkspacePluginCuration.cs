namespace WarpTalk.AssistantService.Domain.Entities;

/// <summary>
/// Marks a workspace whose plugin list has been written at least once, so only that list counts.
/// </summary>
/// <remarks>
/// Its own row rather than "the workspace has any workspace_plugins rows": an Owner who removes
/// every plugin must end up with none, not fall back to the pre-marketplace "every plugin" default.
/// </remarks>
public class WorkspacePluginCuration
{
    public Guid WorkspaceId { get; set; }

    public DateTime CuratedAt { get; set; }

    public Guid? CuratedBy { get; set; }

    /// <summary>What AllowAnyPlugins said when the list was first written.</summary>
    public bool SeededFromAllowAnyPlugins { get; set; }
}
