using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Mappers;

internal static class McpToolAuditMapper
{
    public static PluginToolAudit ToEntity(
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request,
        string resultStatus,
        string? providerResourceRef)
    {
        var argumentsJson = request.Arguments?.ToJsonString();
        var inputSummary = argumentsJson is { Length: > 500 }
            ? argumentsJson[..500]
            : argumentsJson;

        return new PluginToolAudit
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            UserId = userId,
            ConversationId = request.ConversationId,
            AssistantMessageId = request.AssistantMessageId,
            PluginId = pluginId,
            PluginKey = request.PluginKey,
            ToolName = request.ToolName,
            InputSummary = inputSummary,
            ResultStatus = resultStatus,
            ProviderResourceRef = providerResourceRef,
            CreatedAt = DateTime.UtcNow,
        };
    }
}
