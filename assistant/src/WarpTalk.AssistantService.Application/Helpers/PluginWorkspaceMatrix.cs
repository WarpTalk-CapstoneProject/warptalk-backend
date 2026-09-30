using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>One marketplace plugin in one workspace, every layer resolved.</summary>
/// <param name="OnWorkspaceList">
/// Whether the workspace's own list holds it: a curated workspace's rows, or - for one that never
/// edited its list - what it carries over (<see cref="WorkspacePluginAvailability.CarriedOver"/>).
/// </param>
/// <param name="ConnectedUserIds">Active members of the workspace who have connected the plugin.</param>
public sealed record PluginWorkspaceCell(
    Plugin Plugin,
    PlatformWorkspace Workspace,
    PluginWorkspaceVerdict Verdict,
    WorkspacePluginOverride? Override,
    bool OnWorkspaceList,
    IReadOnlyList<Guid> ConnectedUserIds,
    int UsageCount,
    DateTime? LastUsedAt)
{
    /// <summary>
    /// "Workspaces using it": the platform lets the workspace have the plugin AND the workspace
    /// actually has it - on its list, or connected by at least one of its members.
    /// </summary>
    /// <remarks>
    /// Connections count because they are what an admin means by "using". Before this the count was
    /// the workspace's list alone, and almost no workspace has ever edited its list - so the column
    /// said "no workspaces" on every row while members had plugins connected all over the product.
    /// </remarks>
    public bool InUse => Verdict.Allowed && (OnWorkspaceList || ConnectedUserIds.Count > 0);
}

/// <summary>
/// Every (marketplace plugin, workspace) pair, resolved by the same rules the guard applies on each
/// tool call - so the admin pages report what enforcement actually does.
/// </summary>
/// <remarks>
/// A fixed number of reads however many workspaces there are: one workspace-service call for the
/// list (plan, Owner, legacy switch included), one for the memberships of the users who connected,
/// and one query per table here.
/// </remarks>
public static class PluginWorkspaceMatrix
{
    /// <returns>Null when the workspace service could not answer: the matrix is unknown, not empty.</returns>
    public static async Task<IReadOnlyList<PluginWorkspaceCell>?> BuildAsync(
        IUnitOfWork unitOfWork,
        IWorkspaceDirectoryClient directory,
        IReadOnlyList<Plugin> plugins,
        Guid? onlyWorkspaceId,
        CancellationToken ct)
    {
        var workspaces = await directory.ListPlatformWorkspacesAsync(ct);
        if (workspaces is null) return null;
        if (onlyWorkspaceId is { } only) workspaces = workspaces.Where(w => w.WorkspaceId == only).ToList();

        var marketplace = plugins.Where(plugin => plugin.OwnerWorkspaceId is null).ToList();
        if (workspaces.Count == 0 || marketplace.Count == 0) return [];

        // Lists, not sets: EF translates List.Contains into an IN.
        var pluginIds = marketplace.Select(plugin => plugin.Id).ToList();

        var overrides = (await unitOfWork.WorkspacePluginOverrideRepository.FindAsync(
                row => pluginIds.Contains(row.PluginId) && (onlyWorkspaceId == null || row.WorkspaceId == onlyWorkspaceId),
                ct: ct))
            .GroupBy(row => (row.WorkspaceId, row.PluginId))
            .ToDictionary(group => group.Key, group => group.First());

        var curated = (await unitOfWork.WorkspacePluginCurationRepository.FindAsync(
                row => onlyWorkspaceId == null || row.WorkspaceId == onlyWorkspaceId,
                ct: ct))
            .Select(row => row.WorkspaceId)
            .ToHashSet();

        var listRows = (await unitOfWork.WorkspacePluginRepository.FindAsync(
                row => pluginIds.Contains(row.PluginId) && (onlyWorkspaceId == null || row.WorkspaceId == onlyWorkspaceId),
                ct: ct))
            .Select(row => (row.WorkspaceId, row.PluginId))
            .ToHashSet();

        var usage = (await unitOfWork.PluginToolAuditRepository.GetSuccessfulUsageByWorkspaceAsync(pluginIds, onlyWorkspaceId, ct))
            .ToDictionary(row => (row.WorkspaceId, row.PluginId));

        var connected = await ConnectedUsersByWorkspaceAsync(unitOfWork, directory, marketplace, onlyWorkspaceId, ct);
        if (connected is null) return null;

        var cells = new List<PluginWorkspaceCell>(workspaces.Count * marketplace.Count);
        foreach (var workspace in workspaces)
        {
            foreach (var plugin in marketplace)
            {
                var key = (workspace.WorkspaceId, plugin.Id);
                overrides.TryGetValue(key, out var workspaceOverride);
                usage.TryGetValue(key, out var used);

                var onList = curated.Contains(workspace.WorkspaceId)
                    ? listRows.Contains(key)
                    // The transition, stated once: WorkspacePluginAvailability.CarriedOver.
                    : workspace.AllowAnyPlugins && used is not null;

                cells.Add(new PluginWorkspaceCell(
                    plugin,
                    workspace,
                    PluginWorkspaceAccess.Resolve(plugin, workspace.PlanSlug, workspaceOverride),
                    workspaceOverride,
                    onList,
                    connected.TryGetValue(key, out var users) ? users : [],
                    used?.CallCount ?? 0,
                    used?.LastUsedAt));
            }
        }

        return cells;
    }

    /// <summary>
    /// Which of each workspace's active members connected each plugin. Connections are personal - a
    /// user, never a workspace - so they are placed in workspaces through the users' memberships.
    /// </summary>
    /// <remarks>
    /// "Connected" is what the Owner's who-connected list and the member's own page mean by it: an
    /// installed row with <c>connected_at</c>, backed by a grant for the plugin's provider.
    /// </remarks>
    private static async Task<Dictionary<(Guid WorkspaceId, Guid PluginId), IReadOnlyList<Guid>>?> ConnectedUsersByWorkspaceAsync(
        IUnitOfWork unitOfWork,
        IWorkspaceDirectoryClient directory,
        IReadOnlyList<Plugin> plugins,
        Guid? onlyWorkspaceId,
        CancellationToken ct)
    {
        var result = new Dictionary<(Guid, Guid), IReadOnlyList<Guid>>();
        var pluginIds = plugins.Select(plugin => plugin.Id).ToList();

        var installations = await unitOfWork.PluginInstallationRepository.FindAsync(
            installation => pluginIds.Contains(installation.PluginId)
                && installation.Status == PluginConstants.InstallationStatus.Installed
                && installation.ConnectedAt != null,
            ct: ct);
        if (installations.Count == 0) return result;

        var userIds = installations.Select(installation => installation.UserId).Distinct().ToList();
        var providers = plugins.Select(plugin => plugin.Provider).Distinct().ToList();
        var grants = (await unitOfWork.PluginConnectionRepository.FindAsync(
                connection => userIds.Contains(connection.UserId) && providers.Contains(connection.Provider),
                ct: ct))
            .Select(connection => (connection.UserId, connection.Provider))
            .ToHashSet();

        var providerOf = plugins.ToDictionary(plugin => plugin.Id, plugin => plugin.Provider);
        var connectedPairs = installations
            .Where(installation => grants.Contains((installation.UserId, providerOf[installation.PluginId])))
            .Select(installation => (installation.UserId, installation.PluginId))
            .Distinct()
            .ToList();
        if (connectedPairs.Count == 0) return result;

        // One workspace: its member list, which the workspace service already answers. Every
        // workspace: the memberships of just the users who connected something, in one call.
        IReadOnlyList<(Guid UserId, Guid WorkspaceId)>? memberships;
        if (onlyWorkspaceId is { } only)
        {
            var members = await directory.ListActiveMemberUserIdsAsync(only, ct);
            memberships = members?.Select(user => (user, only)).ToList();
        }
        else
        {
            memberships = await directory.ListActiveMembershipsAsync(
                connectedPairs.Select(pair => pair.UserId).Distinct().ToList(), ct);
        }

        if (memberships is null) return null;

        var workspacesOf = memberships
            .GroupBy(pair => pair.UserId)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.WorkspaceId).Distinct().ToList());

        foreach (var group in connectedPairs
                     .SelectMany(pair => workspacesOf.TryGetValue(pair.UserId, out var workspaceIds)
                         ? workspaceIds.Select(workspaceId => (WorkspaceId: workspaceId, pair.PluginId, pair.UserId))
                         : [])
                     .GroupBy(entry => (entry.WorkspaceId, entry.PluginId)))
        {
            result[group.Key] = group.Select(entry => entry.UserId).Distinct().ToList();
        }

        return result;
    }
}
