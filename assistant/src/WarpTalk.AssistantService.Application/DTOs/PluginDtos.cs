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

/// <summary>
/// Why a refresh-token exchange ended the way it did, expressed in provider-neutral terms.
/// </summary>
/// <remarks>
/// This is the whole point of the type: the Infrastructure OAuth client owns the provider's status
/// codes and error bodies, and hands Application a decision it can act on without knowing that
/// Google, HTTP, or <c>invalid_grant</c> exist. Only <see cref="GrantRejected"/> is proof the
/// stored grant is dead; every other failure leaves the connection exactly as it was.
/// </remarks>
public enum PluginOAuthRefreshOutcome
{
    /// <summary>A usable access token came back.</summary>
    Succeeded,

    /// <summary>
    /// The provider refused the refresh token itself - revoked grant, changed password, pruned
    /// token. Nothing but a fresh consent fixes it, so the connection ends here.
    /// </summary>
    GrantRejected,

    /// <summary>
    /// The provider or the network got in the way - outage, timeout, DNS, an unclassified
    /// response. The grant is not proven dead; retrying later is the right move.
    /// </summary>
    ProviderUnavailable,

    /// <summary>The provider throttled us. Transient, and worth telling the caller apart from a plain outage.</summary>
    ProviderRateLimited,
}

/// <summary>
/// Outcome of <see cref="Interfaces.IPluginOAuthClient.RefreshAccessTokenAsync"/>.
/// <see cref="Token"/> is non-null exactly when <see cref="Outcome"/> is
/// <see cref="PluginOAuthRefreshOutcome.Succeeded"/>.
/// </summary>
public record PluginOAuthRefreshResultDto(
    PluginOAuthRefreshOutcome Outcome,
    PluginOAuthTokenDto? Token,
    string? Detail = null)
{
    public bool IsSuccess => Outcome == PluginOAuthRefreshOutcome.Succeeded;

    public static PluginOAuthRefreshResultDto Succeeded(PluginOAuthTokenDto token) =>
        new(PluginOAuthRefreshOutcome.Succeeded, token);

    public static PluginOAuthRefreshResultDto GrantRejected(string detail) =>
        new(PluginOAuthRefreshOutcome.GrantRejected, null, detail);

    public static PluginOAuthRefreshResultDto ProviderUnavailable(string detail) =>
        new(PluginOAuthRefreshOutcome.ProviderUnavailable, null, detail);

    public static PluginOAuthRefreshResultDto ProviderRateLimited(string detail) =>
        new(PluginOAuthRefreshOutcome.ProviderRateLimited, null, detail);
}

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
