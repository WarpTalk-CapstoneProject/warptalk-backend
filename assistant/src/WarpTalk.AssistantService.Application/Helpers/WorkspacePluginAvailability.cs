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
        IReadOnlySet<Guid> addedPluginIds)
    {
        WorkspaceId = workspaceId;
        IsCurated = isCurated;
        LegacyAllowsEveryPlugin = legacyAllowsEveryPlugin;
        _addedPluginIds = addedPluginIds;
    }

    public Guid WorkspaceId { get; }

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
