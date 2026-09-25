using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;

namespace WarpTalk.AssistantService.Application.Mappers;

public static class McpConfirmationTokenPayloadMatcher
{
    public static bool Matches(
        McpConfirmationTokenPayloadDto payload,
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request)
    {
        return payload.UserId == userId
            && payload.WorkspaceId == request.WorkspaceId
            && payload.PluginId == pluginId
            && string.Equals(payload.PluginKey, request.PluginKey, StringComparison.Ordinal)
            && string.Equals(payload.ToolName, request.ToolName, StringComparison.Ordinal)
            && string.Equals(payload.ArgumentHash, McpConfirmationArgumentHasher.Hash(request.Arguments), StringComparison.Ordinal);
    }
}
