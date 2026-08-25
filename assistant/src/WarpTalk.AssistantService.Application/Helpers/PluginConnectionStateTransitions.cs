using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Helpers;

internal static class PluginConnectionStateTransitions
{
    internal static async Task<Result> MarkExpiredAsync(
        IUnitOfWork unitOfWork,
        PluginConnection connection,
        CancellationToken ct)
    {
        connection.Status = PluginConstants.ConnectionStatus.Expired;
        connection.UpdatedAt = DateTime.UtcNow;
        unitOfWork.PluginConnectionRepository.Update(connection);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Failure(
            "Reconnect your provider account.",
            PluginConstants.ErrorCodes.ConnectionRequired);
    }
}
