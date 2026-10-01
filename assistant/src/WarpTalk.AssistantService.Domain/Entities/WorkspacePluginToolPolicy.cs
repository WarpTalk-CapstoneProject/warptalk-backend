namespace WarpTalk.AssistantService.Domain.Entities;

/// <summary>
/// A workspace Owner's rule for one tool of one plugin in that workspace: ask every time, or never.
/// </summary>
/// <remarks>
/// Only the rules an Owner set are stored. No row means "member's choice" - each member's own
/// per-tool policy (WT-687) applies unchanged. A rule never loosens a member's choice: the stricter
/// of the two is what WarpBot gets, so there is no "allow" here.
/// <para>
/// Its own table rather than a column on <see cref="WorkspacePlugin"/>: a workspace whose plugin
/// list was never curated has no rows there, and an Owner can still need to block a tool in it.
/// </para>
/// </remarks>
public class WorkspacePluginToolPolicy
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PluginId { get; set; }

    /// <summary>The tool's name as the plugin's manifest declares it.</summary>
    public string ToolName { get; set; } = null!;

    /// <summary><c>approval</c> or <c>blocked</c>.</summary>
    public string Policy { get; set; } = null!;

    public Guid SetBy { get; set; }

    public DateTime SetAt { get; set; }
}
