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
    /// caller must keep whatever it already stored.
    /// <para>
    /// Failure is a <em>return value</em>, not an exception: a refresh that fails because the
    /// provider is having a bad minute is an ordinary, expected outcome, and the caller has to act
    /// on it differently from a grant the provider has actually rejected. Implementations classify
    /// the provider's own status code and error body into
    /// <see cref="PluginOAuthRefreshOutcome"/> so no HTTP detail crosses into Application.
    /// Implementations must not report <see cref="PluginOAuthRefreshOutcome.GrantRejected"/>
    /// unless the provider explicitly refused the refresh token - anything ambiguous is transient,
    /// because ending a connection costs the user a browser round trip and retrying costs nothing.
    /// </para>
    /// </remarks>
    Task<PluginOAuthRefreshResultDto> RefreshAccessTokenAsync(
        Plugin plugin,
        string refreshToken,
        CancellationToken ct = default);
}
