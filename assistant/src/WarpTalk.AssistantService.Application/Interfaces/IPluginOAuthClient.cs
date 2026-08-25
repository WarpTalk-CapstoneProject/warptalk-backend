using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginOAuthClient
{
    string BuildAuthorizationUrl(Plugin plugin, IReadOnlyList<string> scopes, string state);

    Task<PluginOAuthTokenDto> ExchangeCodeAsync(
        Plugin plugin,
        string code,
        CancellationToken ct = default);

    /// <summary>
    /// Exchanges a stored refresh token for a fresh access token.
    /// </summary>
    /// <remarks>
    /// Providers commonly answer without a new refresh token (Google only issues one on the first
    /// consent), so <see cref="PluginOAuthTokenDto.RefreshToken"/> may be <c>null</c> and the
    /// caller must keep whatever it already stored. Throws when the provider rejects the grant.
    /// </remarks>
    Task<PluginOAuthTokenDto> RefreshAccessTokenAsync(
        Plugin plugin,
        string refreshToken,
        CancellationToken ct = default);
}
