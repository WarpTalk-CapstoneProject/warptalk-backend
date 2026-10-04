using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Reads <c>assistant_tool_calls</c> for Insights (wave 4): every WarpBot tool call, built-in, web
/// search and plugin.
/// </summary>
public interface IAssistantToolInsightsService
{
    /// <summary>
    /// One workspace's tool usage. The workspace's Owner or Admin only, by the same membership check
    /// as the plugin audits (<c>GET /assistant/mcp/tools/audits</c>).
    /// </summary>
    /// <param name="from">Window start, UTC, inclusive. Defaults to <paramref name="to"/> - 30 days.</param>
    /// <param name="to">Window end, UTC, exclusive. Defaults to now.</param>
    Task<Result<WorkspaceToolInsightsDto>> GetWorkspaceAsync(
        Guid workspaceId, Guid callerUserId, DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>
    /// Tool usage across every workspace. Authorisation is the controller's staff permission
    /// (<c>plugins.read</c>); this method checks nothing.
    /// </summary>
    Task<Result<AdminToolInsightsDto>> GetPlatformAsync(DateTime? from, DateTime? to, CancellationToken ct = default);
}
