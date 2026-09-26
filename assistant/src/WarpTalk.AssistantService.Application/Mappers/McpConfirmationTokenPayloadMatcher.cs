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
        return MatchesAction(payload, userId, pluginId, request)
            && MatchesArguments(payload, request);
    }

    /// <summary>
    /// Whether the token was issued to this user, in this workspace, for this plugin's tool —
    /// everything except what the call would do with it.
    /// </summary>
    public static bool MatchesAction(
        McpConfirmationTokenPayloadDto payload,
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request)
    {
        return payload.UserId == userId
            && payload.WorkspaceId == request.WorkspaceId
            && payload.PluginId == pluginId
            && string.Equals(payload.PluginKey, request.PluginKey, StringComparison.Ordinal)
            && string.Equals(payload.ToolName, request.ToolName, StringComparison.Ordinal);
    }

    public static bool MatchesArguments(McpConfirmationTokenPayloadDto payload, McpToolExecutionRequest request)
    {
        return string.Equals(
            payload.ArgumentHash,
            McpConfirmationArgumentHasher.Hash(request.Arguments),
            StringComparison.Ordinal);
    }
}
