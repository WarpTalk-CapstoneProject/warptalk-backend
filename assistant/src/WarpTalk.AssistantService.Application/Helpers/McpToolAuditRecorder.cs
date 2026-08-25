using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Helpers;

internal static class McpToolAuditRecorder
{
    public static async Task RecordAsync(
        IUnitOfWork unitOfWork,
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request,
        string resultStatus,
        string? providerResourceRef,
        CancellationToken ct)
    {
        await unitOfWork.PluginToolAuditRepository.AddAsync(
            McpToolAuditMapper.ToEntity(userId, pluginId, request, resultStatus, providerResourceRef),
            ct);
        await unitOfWork.SaveChangesAsync(ct);
    }

    public static async Task<Result<McpToolExecutionResult>> RecordFailureAsync(
        IUnitOfWork unitOfWork,
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request,
        string errorCode,
        string message,
        CancellationToken ct,
        string? confirmationToken = null)
    {
        await RecordAsync(unitOfWork, userId, pluginId, request, errorCode, null, ct);

        return Result.Success(McpToolExecutionResultMapper.ToFailure(errorCode, message, confirmationToken));
    }
}
