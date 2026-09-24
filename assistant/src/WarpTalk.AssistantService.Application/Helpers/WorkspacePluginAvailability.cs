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
/// workspace that has never been curated has no list rows, so its list is what it CARRIES OVER
/// from before the marketplace (<see cref="CarriedOver"/>): the marketplace plugins its members
/// have actually used there, while the legacy AllowAnyPlugins switch is on - and nothing when it
/// is off.
/// </para>
/// <para>
/// It used to carry over EVERY marketplace plugin while the switch was on. That made the Owner's
/// page list the whole marketplace under "In this workspace" for a workspace that had chosen
/// nothing, and made every new marketplace row appear in every such workspace the moment an admin
/// created it. A list the Owner never made is not the workspace's list; the plugins its members
/// were really using are, and those are the ones nobody may lose on deploy.
/// </para>
/// </remarks>
public sealed class WorkspacePluginAvailability
{
    private readonly IReadOnlySet<Guid> _addedPluginIds;

    public WorkspacePluginAvailability(
        Guid workspaceId,
        bool isCurated,
        IReadOnlySet<Guid> addedPluginIds,
        bool callerIsOwner = false)
    {
        WorkspaceId = workspaceId;
        IsCurated = isCurated;
        _addedPluginIds = addedPluginIds;
        CallerIsOwner = callerIsOwner;
    }

    /// <summary>
    /// What a workspace that never edited its plugin list has: the plugins its members have used
    /// there, while the legacy AllowAnyPlugins switch is on; nothing while it is off.
    /// </summary>
    /// <remarks>
    /// The one statement of the transition, shared by the guard (every tool call), the first edit
    /// that turns it into real list rows, and the admin catalog's workspace count - so the three
    /// cannot disagree about which workspace has which plugin.
    /// </remarks>
    public static IReadOnlySet<Guid> CarriedOver(bool allowAnyPlugins, IReadOnlySet<Guid> usedPluginIds) =>
        allowAnyPlugins ? usedPluginIds : new HashSet<Guid>();

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

        return _addedPluginIds.Contains(plugin.Id)
            ? WorkspacePluginConstants.Availability.Added
            : WorkspacePluginConstants.Availability.NotAdded;
    }

    public bool IsUsable(Plugin plugin) =>
        WorkspacePluginConstants.Availability.IsUsable(Of(plugin));
}
