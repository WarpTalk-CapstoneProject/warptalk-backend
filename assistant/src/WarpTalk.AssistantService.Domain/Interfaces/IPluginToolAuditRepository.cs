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

    /// <summary>
    /// A newest-first page of one workspace's plugin tool usage. WT-646.
    /// </summary>
    /// <remarks>
    /// Bespoke rather than <see cref="IGenericRepository{T}.FindAsync"/> because this needs an
    /// ordering and a page, and an audit table grows without bound - a predicate that materialises
    /// every row a busy workspace ever wrote is not a read path, it is an outage.
    /// <para>
    /// Sibling of <see cref="ListForPluginAsync"/>: that one serves the platform admin looking at
    /// one catalog row across every workspace, this one serves a workspace Owner/Admin looking at
    /// their own workspace across every plugin. Neither subsumes the other, so both exist.
    /// </para>
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

    /// <summary>
    /// How many audit rows a catalog row would take with it if it were deleted. WT-646.
    /// </summary>
    /// <remarks>
    /// <c>plugin_tool_audits_plugin_id_fkey</c> is <c>ON DELETE CASCADE</c>, so this number is not
    /// informational - it is the number of compliance records a hard delete destroys silently, in
    /// the same statement and with no error. The catalog admin service asks before deleting for
    /// that reason and for no other.
    /// </remarks>
    Task<int> CountForPluginAsync(Guid pluginId, CancellationToken ct = default);

    /// <summary>
    /// For each plugin, how many distinct members have run one of its tools in this workspace.
    /// </summary>
    /// <remarks>
    /// What the workspace Plugins page shows beside a plugin. Connections are personal - one grant,
    /// every workspace - so the audit trail is the only workspace-scoped evidence of use there is.
    /// Only successful calls count; a refusal is not use.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, int>> CountDistinctUsersByPluginForWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// The plugins members have successfully used in one workspace - what a workspace that never
    /// edited its plugin list carries over while AllowAnyPlugins is on.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetPluginIdsUsedInWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Successful calls grouped by (workspace, plugin), for the given plugins - and one workspace
    /// when <paramref name="workspaceId"/> is set. Calls with no workspace are not counted.
    /// </summary>
    Task<IReadOnlyList<WorkspacePluginUsage>> GetSuccessfulUsageByWorkspaceAsync(
        IReadOnlyCollection<Guid> pluginIds,
        Guid? workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// Per user, the last successful call and how many there were, for one plugin in one workspace.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PluginUsageByUser>> GetUsageByUserAsync(
        Guid workspaceId,
        Guid pluginId,
        CancellationToken ct = default);
}

/// <summary>One member's successful use of one plugin in one workspace.</summary>
public sealed record PluginUsageByUser(DateTime LastUsedAt, int CallCount);

/// <summary>Successful calls of one plugin inside one workspace.</summary>
public sealed record WorkspacePluginUsage(Guid WorkspaceId, Guid PluginId, int CallCount, DateTime LastUsedAt);
