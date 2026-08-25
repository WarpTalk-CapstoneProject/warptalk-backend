using System.Security.Cryptography;
using System.Text;
using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Helpers;

internal static class McpConfirmationTokenFactory
{
    public static string Create(Guid userId, McpToolExecutionRequest request)
    {
        var raw = $"{userId}:{request.WorkspaceId}:{request.PluginKey}:{request.ToolName}:{request.Arguments?.ToJsonString()}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    public static bool Matches(Guid userId, McpToolExecutionRequest request, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var expected = Encoding.UTF8.GetBytes(Create(userId, request));
        var actual = Encoding.UTF8.GetBytes(token);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
