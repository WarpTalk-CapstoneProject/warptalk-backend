namespace WarpTalk.AssistantService.Application.DTOs;

public record McpConfirmationTokenPayloadDto(
    Guid TokenId,
    Guid UserId,
    Guid? WorkspaceId,
    Guid PluginId,
    string PluginKey,
    string ToolName,
    string ArgumentHash,
    DateTime ExpiresAt);
