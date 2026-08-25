using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Mappers;

public static class McpConfirmationTokenMapper
{
    public static PluginConfirmationToken ToEntity(
        Guid userId,
        Guid pluginId,
        McpToolExecutionRequest request,
        DateTime createdAt,
        DateTime expiresAt)
    {
        return new PluginConfirmationToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            WorkspaceId = request.WorkspaceId,
            PluginId = pluginId,
            PluginKey = request.PluginKey,
            ToolName = request.ToolName,
            ArgumentHash = McpConfirmationArgumentHasher.Hash(request.Arguments),
            ExpiresAt = expiresAt,
            CreatedAt = createdAt,
        };
    }

    public static McpConfirmationTokenPayloadDto ToPayload(PluginConfirmationToken entity)
    {
        return new McpConfirmationTokenPayloadDto(
            entity.Id,
            entity.UserId,
            entity.WorkspaceId,
            entity.PluginId,
            entity.PluginKey,
            entity.ToolName,
            entity.ArgumentHash,
            entity.ExpiresAt);
    }
}
