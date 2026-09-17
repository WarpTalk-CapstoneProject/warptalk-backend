using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IMcpToolOrchestrator
{
    /// <param name="excludedPluginKeys">
    /// Plugins the user switched off for this conversation. WT-687. Null or empty offers every
    /// installed plugin, which is what a caller older than the setting gets.
    /// </param>
    Task<Result<IReadOnlyList<McpToolDescriptorDto>>> ListAvailableToolsAsync(
        Guid userId,
        Guid? workspaceId,
        IReadOnlyCollection<string>? excludedPluginKeys = null,
        CancellationToken ct = default);

    Task<Result<McpToolExecutionResult>> ExecuteAsync(Guid userId, McpToolExecutionRequest request, CancellationToken ct = default);
}
