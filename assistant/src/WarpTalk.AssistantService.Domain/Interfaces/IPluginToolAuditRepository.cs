using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IPluginToolAuditRepository : IGenericRepository<PluginToolAudit>
{
    /// <summary>
    /// A newest-first page of one workspace's plugin tool usage. WT-646.
    /// </summary>
    /// <remarks>
    /// Bespoke rather than <see cref="IGenericRepository{T}.FindAsync"/> because this needs an
    /// ordering and a page, and an audit table grows without bound - a predicate that materialises
    /// every row a busy workspace ever wrote is not a read path, it is an outage.
    /// </remarks>
    /// <param name="pluginKey">Optional. Narrows to one plugin.</param>
    /// <param name="userId">Optional. Narrows to one member.</param>
    Task<IReadOnlyList<PluginToolAudit>> ListForWorkspaceAsync(
        Guid workspaceId,
        string? pluginKey,
        Guid? userId,
        int skip,
        int take,
        CancellationToken ct = default);
}
