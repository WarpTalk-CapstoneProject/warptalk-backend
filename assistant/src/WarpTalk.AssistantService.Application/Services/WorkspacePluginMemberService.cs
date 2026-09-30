using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <inheritdoc cref="IWorkspacePluginMemberService"/>
/// <remarks>
/// WHY THE MEMBER LIST IS ASKED FOR. Installations and connections are personal: they carry a user,
/// never a workspace. Filtering them by plugin alone would list every user on the platform who
/// connected Notion, to the Owner of any workspace that has Notion - so the set is intersected with
/// this workspace's ACTIVE members, from the workspace service, before anything is read. If that
/// list cannot be had, the answer is a 503, never "nobody" and never "everybody".
/// <para>
/// WHAT "CONNECTED" MEANS matches the member's own Plugins page (<c>PluginCatalogItemMapper</c>): an
/// installed row with a <c>connected_at</c>. The grant is keyed by provider, so its status - connected,
/// expired or revoked - is read from the member's connection for the plugin's provider.
/// </para>
/// </remarks>
public class WorkspacePluginMemberService : IWorkspacePluginMemberService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMembershipClient _membershipClient;
    private readonly IWorkspaceDirectoryClient _directoryClient;

    public WorkspacePluginMemberService(
        IUnitOfWork unitOfWork,
        IWorkspaceMembershipClient membershipClient,
        IWorkspaceDirectoryClient directoryClient)
    {
        _unitOfWork = unitOfWork;
        _membershipClient = membershipClient;
        _directoryClient = directoryClient;
    }

    public async Task<Result<IReadOnlyList<WorkspacePluginMemberDto>>> ListConnectedMembersAsync(
        Guid workspaceId,
        Guid callerId,
        string pluginKey,
        CancellationToken ct = default)
    {
        // Role first, so a Member cannot learn which plugins exist here from a 404 versus a 403.
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, callerId, ct);
        if (!membership.IsOwnerOrAdmin)
            return Result.Failure<IReadOnlyList<WorkspacePluginMemberDto>>(
                WorkspacePluginConstants.Messages.OwnerOrAdminOnly,
                PluginConstants.ErrorCodes.PermissionDenied);

        // A marketplace row, or this workspace's own private plugin; another workspace's private
        // plugin is simply not one of this workspace's.
        var key = pluginKey?.Trim() ?? string.Empty;
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(
            p => p.PluginKey == key && (p.OwnerWorkspaceId == null || p.OwnerWorkspaceId == workspaceId),
            ct: ct);
        if (plugin is null)
            return Result.Failure<IReadOnlyList<WorkspacePluginMemberDto>>(
                "Unknown plugin.",
                PluginConstants.ErrorCodes.UnknownPlugin);

        var memberIds = await _directoryClient.ListActiveMemberUserIdsAsync(workspaceId, ct);
        if (memberIds is null)
            return Result.Failure<IReadOnlyList<WorkspacePluginMemberDto>>(
                WorkspacePluginConstants.Messages.MembersUnavailable,
                WorkspacePluginConstants.ErrorCodes.MembersUnavailable);
        if (memberIds.Count == 0)
            return Result.Success<IReadOnlyList<WorkspacePluginMemberDto>>([]);

        // Lists, not sets: EF translates List.Contains into an IN.
        var members = memberIds.ToList();
        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(
            installation => installation.PluginId == plugin.Id
                && installation.Status == PluginConstants.InstallationStatus.Installed
                && installation.ConnectedAt != null
                && members.Contains(installation.UserId),
            ct: ct);
        if (installations.Count == 0)
            return Result.Success<IReadOnlyList<WorkspacePluginMemberDto>>([]);

        var connectedUsers = installations.Select(installation => installation.UserId).Distinct().ToList();
        var connections = await _unitOfWork.PluginConnectionRepository.FindAsync(
            connection => connection.Provider == plugin.Provider && connectedUsers.Contains(connection.UserId),
            ct: ct);
        var statusByUser = connections
            .GroupBy(connection => connection.UserId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(c => c.UpdatedAt).First().Status);
        var usage = await _unitOfWork.PluginToolAuditRepository.GetUsageByUserAsync(workspaceId, plugin.Id, ct);

        var rows = installations
            // No grant left behind the installation means nothing is connected, whatever
            // connected_at still says: the member's own page reads not_connected for it too.
            .Where(installation => statusByUser.ContainsKey(installation.UserId))
            .Select(installation =>
            {
                usage.TryGetValue(installation.UserId, out var used);
                return new WorkspacePluginMemberDto(
                    installation.UserId,
                    statusByUser[installation.UserId],
                    installation.ConnectedAt!.Value,
                    used?.LastUsedAt,
                    used?.CallCount ?? 0);
            })
            .OrderByDescending(row => row.LastUsedAt.HasValue)
            .ThenByDescending(row => row.LastUsedAt)
            .ThenByDescending(row => row.ConnectedAt)
            .ToList();

        return Result.Success<IReadOnlyList<WorkspacePluginMemberDto>>(rows);
    }
}
