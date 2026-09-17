namespace WarpTalk.AssistantService.Domain.Entities;

/// <summary>
/// A marketplace plugin a workspace's Owner has added to that workspace.
/// </summary>
/// <remarks>
/// Only consulted once the workspace has a <see cref="WorkspacePluginCuration"/>. Before that the
/// workspace is still judged by the workspace service's AllowAnyPlugins switch, which is how the
/// marketplace shipped without taking a plugin away from anyone.
/// </remarks>
public class WorkspacePlugin
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PluginId { get; set; }

    /// <summary>Null when the transition seeded the row rather than a person adding it.</summary>
    public Guid? AddedBy { get; set; }

    public DateTime AddedAt { get; set; }
}
