using WarpTalk.AssistantService.Application.DTOs;

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
}
