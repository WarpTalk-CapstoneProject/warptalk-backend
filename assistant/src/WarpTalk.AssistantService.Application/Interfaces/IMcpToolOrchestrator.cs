using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IMcpToolOrchestrator
{
    /// <param name="excludedPluginKeys">
    /// Plugins the user switched off for this conversation. WT-687. Null or empty offers every
    /// installed plugin, which is what a caller older than the setting gets.
    /// </param>
    /// <param name="callerIsPlatformStaff">
    /// The caller holds <c>warpbot.use</c>. Only consulted when <paramref name="workspaceId"/> is
    /// absent: then the admin portal's WarpBot is asking, and staff get every active marketplace
    /// plugin they installed. Anyone else without a workspace still gets nothing.
    /// </param>
    Task<Result<IReadOnlyList<McpToolDescriptorDto>>> ListAvailableToolsAsync(
        Guid userId,
        Guid? workspaceId,
        IReadOnlyCollection<string>? excludedPluginKeys = null,
        CancellationToken ct = default,
        bool callerIsPlatformStaff = false);

    /// <summary>
    /// The same plugin tools <see cref="ListAvailableToolsAsync"/> offers WarpBot, grouped by
    /// plugin. Each tool's Policy is the member's own choice and WorkspacePolicy the workspace
    /// Owner's rule; a tool whose stricter of the two is blocked is already left out.
    /// </summary>
    /// <remarks>
    /// For a caller that has already resolved <paramref name="availability"/> for a member of the
    /// workspace, so the membership check is not made twice.
    /// </remarks>
    Task<IReadOnlyList<OfferedPluginToolsDto>> ListOfferedPluginToolsAsync(
        Guid userId,
        WorkspacePluginAvailability availability,
        IReadOnlyCollection<string>? excludedPluginKeys = null,
        CancellationToken ct = default);

    Task<Result<McpToolExecutionResult>> ExecuteAsync(
        Guid userId,
        McpToolExecutionRequest request,
        CancellationToken ct = default,
        bool callerIsPlatformStaff = false);
}
