using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Constants;

namespace WarpTalk.AssistantService.Application.Mappers;

internal static class McpToolExecutionResultMapper
{
    public static McpToolExecutionResult ToFailure(
        string errorCode,
        string message,
        string? confirmationToken = null)
    {
        return new McpToolExecutionResult(false, errorCode, message, null, null, confirmationToken);
    }

    public static McpToolExecutionResult ToConnectionRequiredFailure(
        PluginDefinitionDto plugin,
        string connectionStatus,
        string? connectedAccountEmail,
        string message)
    {
        return new McpToolExecutionResult(
            false,
            PluginConstants.ErrorCodes.ConnectionRequired,
            message,
            null,
            null,
            null,
            plugin.Key,
            plugin.Label,
            connectionStatus,
            connectedAccountEmail);
    }
}
