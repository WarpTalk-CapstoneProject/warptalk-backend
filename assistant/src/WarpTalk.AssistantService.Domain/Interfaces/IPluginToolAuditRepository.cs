using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IPluginToolAuditRepository : IGenericRepository<PluginToolAudit>
{
    /// <summary>
    /// A page of one plugin's tool invocations, newest first, filtered and counted in the database.
    /// </summary>
    /// <remarks>
    /// This exists rather than being composed from <see cref="IGenericRepository{T}.FindAsync"/>
    /// because that method materialises every matching row. <c>plugin_tool_audits</c> grows with
    /// every tool call every user makes and is never pruned, so paging it in memory would load the
    /// whole history to return fifty rows - a query that is fine on the day it ships and is an
    /// outage later.
    /// </remarks>
    /// <param name="pluginId">The catalog row whose audits to read.</param>
    /// <param name="userId">Optional filter on the acting user.</param>
    /// <param name="resultStatus">
    /// Optional filter on <c>result_status</c> - <c>"ok"</c> or an error code from
    /// <c>PluginConstants.ErrorCodes</c>.
    /// </param>
    /// <param name="skip">Rows to skip; the caller has already turned a page number into this.</param>
    /// <param name="take">Page size.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The page, and the total number of rows matching the filters.</returns>
    Task<(IReadOnlyList<PluginToolAudit> Items, int TotalCount)> ListForPluginAsync(
        Guid pluginId,
        Guid? userId,
        string? resultStatus,
        int skip,
        int take,
        CancellationToken ct = default);
}
