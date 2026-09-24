using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// Which plugins one workspace has, read once and asked about per plugin.
/// </summary>
/// <remarks>
/// The rule: a plugin is usable in a workspace iff it is in that workspace's list, or it is a
/// private plugin that workspace owns.
/// <para>
/// "That workspace's list" has two readings, and <see cref="IsCurated"/> says which applies. A
/// workspace whose Owner has written the list at least once is judged by the list alone. A
/// workspace that has never been curated is still judged by the pre-marketplace switch
/// (AllowAnyPlugins): on means every marketplace plugin, off means none. That is the transition,
/// and it is what lets the marketplace ship without a member losing a plugin they used yesterday.
/// </para>
/// </remarks>
public sealed class WorkspacePluginAvailability
{
    private readonly IReadOnlySet<Guid> _addedPluginIds;

    public WorkspacePluginAvailability(
        Guid workspaceId,
        bool isCurated,
        bool legacyAllowsEveryPlugin,
        IReadOnlySet<Guid> addedPluginIds,
        bool callerIsOwner = false)
    {
        WorkspaceId = workspaceId;
        IsCurated = isCurated;
        LegacyAllowsEveryPlugin = legacyAllowsEveryPlugin;
        _addedPluginIds = addedPluginIds;
        CallerIsOwner = callerIsOwner;
    }

    public Guid WorkspaceId { get; }

    /// <summary>
    /// Whether the member this was resolved for is the workspace's Owner - who adds a not-added
    /// marketplace plugin rather than asking for it. Always false when resolved without a caller.
    /// </summary>
    /// <remarks>
    /// The one thing here about the asker rather than the workspace. It rides along because the
    /// member check that produces this object has just read the caller's role; asking again for it
    /// would be a second workspace-service round trip on every catalog listing.
    /// </remarks>
    public bool CallerIsOwner { get; }

    /// <summary>Whether the Owner has written this workspace's plugin list at least once.</summary>
    public bool IsCurated { get; }

    /// <summary>
    /// What AllowAnyPlugins said. Only consulted while <see cref="IsCurated"/> is false; always
    /// false once it is, so a stale switch can never widen an explicit list.
    /// </summary>
    public bool LegacyAllowsEveryPlugin { get; }

    /// <returns>
    /// <see cref="WorkspacePluginConstants.Availability.Private"/>,
    /// <see cref="WorkspacePluginConstants.Availability.Added"/> or
    /// <see cref="WorkspacePluginConstants.Availability.NotAdded"/>.
    /// </returns>
    public string Of(Plugin plugin)
    {
        if (plugin.OwnerWorkspaceId is { } owner)
        {
            // Another workspace's private plugin is never available here, whatever the list or the
            // legacy switch says: it is not in the marketplace, so neither can reach it.
            return owner == WorkspaceId
                ? WorkspacePluginConstants.Availability.Private
                : WorkspacePluginConstants.Availability.NotAdded;
        }

        var added = IsCurated ? _addedPluginIds.Contains(plugin.Id) : LegacyAllowsEveryPlugin;
        return added
            ? WorkspacePluginConstants.Availability.Added
            : WorkspacePluginConstants.Availability.NotAdded;
    }

    public bool IsUsable(Plugin plugin) =>
        WorkspacePluginConstants.Availability.IsUsable(Of(plugin));
}
