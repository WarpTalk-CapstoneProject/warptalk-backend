using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IWorkspacePluginRepository : IGenericRepository<WorkspacePlugin>
{
    /// <summary>How many workspaces have each plugin in their list, for the admin catalog.</summary>
    /// <remarks>
    /// Counts explicit lists only. A workspace that has never curated its list is still on the
    /// pre-marketplace "every plugin" default and has no rows here to count.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, int>> CountWorkspacesByPluginAsync(CancellationToken ct = default);
}
