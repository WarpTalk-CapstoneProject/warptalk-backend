using System.Text.Json.Nodes;

namespace WarpTalk.AssistantService.Application.DTOs;

public record PluginDefinitionDto(
    Guid Id,
    string Key,
    string Label,
    string Description,
    string? AvatarUrl,
    IReadOnlyList<string> RequiredScopes,
    IReadOnlyList<McpToolDescriptorDto> Tools);

public record PluginCatalogItemDto(
    string Key,
    string Label,
    string Description,
    string? AvatarUrl,
    IReadOnlyList<string> RequiredScopes,
    string InstallationStatus,
    string ConnectionStatus,
    string? ConnectedAccountEmail,
    IReadOnlyList<McpToolDescriptorDto> Tools);

public record InstallPluginRequest();

public record PluginConnectionStatusDto(
    string PluginKey,
    string Status,
    string? ProviderEmail,
    IReadOnlyList<string> GrantedScopes);

public record PluginConnectUrlDto(string Url);

public record PluginOAuthStateDto(Guid UserId, string PluginKey);

public record PluginOAuthTokenDto(
    string? ProviderAccountId,
    string? ProviderEmail,
    IReadOnlyList<string> GrantedScopes,
    string AccessToken,
    string? RefreshToken,
    DateTime? AccessTokenExpiresAt);

public record McpToolDescriptorDto(
    string Name,
    string PluginKey,
    string Label,
    string Description,
    string Effect,
    IReadOnlyList<string> RequiredScopes,
    JsonObject Parameters);

public record McpToolExecutionRequest(
    Guid? WorkspaceId,
    string PluginKey,
    string ToolName,
    JsonObject? Arguments,
    Guid? ConversationId,
    Guid? AssistantMessageId,
    string? ConfirmationToken);

public record McpToolExecutionResult(
    bool IsSuccess,
    string? ErrorCode,
    string? Message,
    JsonObject? Data,
    string? ProviderResourceRef,
    string? ConfirmationToken);
