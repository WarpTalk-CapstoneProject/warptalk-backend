using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IMcpConfirmationTokenService
{
    Task<Result<string>> CreateAsync(
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request,
        CancellationToken ct = default);

    Task<Result> ValidateAndConsumeAsync(
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request,
        string token,
        CancellationToken ct = default);
}
