using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The workspace-scoped read side of <c>plugin_tool_audits</c>. WT-646.
/// </summary>
/// <remarks>
/// The table has been written by <c>McpToolAuditRecorder</c> since plugins shipped and read by
/// nothing, which meant a workspace could set a plugin policy and then have no way to see whether
/// it was doing anything. This is the Owner/Admin view of their own workspace; the system-admin
/// view scoped to one plugin lives elsewhere.
/// </remarks>
public interface IPluginToolAuditQueryService
{
    /// <summary>
    /// A newest-first page of plugin tool usage in one workspace.
    /// </summary>
    /// <param name="callerUserId">
    /// Authorised as an active Owner or Admin of <paramref name="workspaceId"/>. An ordinary
    /// member is refused: these rows say what every colleague asked a plugin to do.
    /// </param>
    Task<Result<IReadOnlyList<PluginToolAuditDto>>> ListWorkspaceAuditsAsync(
        Guid workspaceId,
        Guid callerUserId,
        string? pluginKey,
        Guid? userId,
        int skip,
        int take,
        CancellationToken ct = default);
}
