using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IWorkspacePluginRepository : IGenericRepository<WorkspacePlugin>
{
    /// <summary>
    /// How many curated workspaces have each plugin in their list, for the admin catalog.
    /// </summary>
    /// <remarks>
    /// Counts curated lists only - rows whose workspace has a <see cref="WorkspacePluginCuration"/>,
    /// seeded rows included. A workspace that has never curated its list is still on the
    /// pre-marketplace AllowAnyPlugins default, which lives in the workspace service; it has no rows
    /// here and is not counted, whatever that switch says.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, int>> CountWorkspacesByPluginAsync(CancellationToken ct = default);
}
