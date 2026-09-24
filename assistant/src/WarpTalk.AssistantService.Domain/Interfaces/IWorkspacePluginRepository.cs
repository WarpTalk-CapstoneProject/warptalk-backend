using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IWorkspacePluginRepository : IGenericRepository<WorkspacePlugin>
{
    /// <summary>How many curated workspaces have each plugin in their list, for the admin catalog.</summary>
    /// <remarks>
    /// Curated lists only - rows whose workspace has a <see cref="WorkspacePluginCuration"/>. A
    /// workspace that never edited its list is counted from its carried-over usage instead; see
    /// <see cref="IPluginToolAuditRepository.GetPluginIdsUsedByUncuratedWorkspaceAsync"/>.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, int>> CountWorkspacesByPluginAsync(CancellationToken ct = default);
}
