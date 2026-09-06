using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IMcpToolOrchestrator
{
    Task<Result<IReadOnlyList<McpToolDescriptorDto>>> ListAvailableToolsAsync(Guid userId, Guid? workspaceId, CancellationToken ct = default);
    Task<Result<McpToolExecutionResult>> ExecuteAsync(Guid userId, McpToolExecutionRequest request, CancellationToken ct = default);
}
