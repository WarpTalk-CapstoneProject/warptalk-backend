using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginOAuthClient
{
    /// <summary>
    /// Lets a provider add whatever its flow needs to survive the browser round trip, before the
    /// state is sealed.
    /// </summary>
    /// <remarks>
    /// This exists because PKCE has a chicken-and-egg shape: the verifier must be remembered until
    /// the token request, but the challenge derived from it goes into a URL that also carries the
    /// sealed state. Splitting "produce the flow secrets" from "build the URL" lets the caller seal
    /// the state in between, so nothing has to be stored server-side and a callback can be
    /// completed by whichever replica receives it.
    /// <para>
    /// A provider with nothing to carry returns <paramref name="state"/> unchanged.
    /// </para>
    /// </remarks>
    PluginOAuthStateDto PrepareState(Plugin plugin, PluginOAuthStateDto state);

    /// <param name="protectedState">The sealed state, already URL-safe, to put in the request.</param>
    /// <param name="flowState">
    /// The same state unsealed, so the provider can derive request parameters from what
    /// <see cref="PrepareState"/> produced - the PKCE challenge, for instance.
    /// </param>
    string BuildAuthorizationUrl(
        Plugin plugin,
        IReadOnlyList<string> scopes,
        string protectedState,
        PluginOAuthStateDto flowState);

    /// <param name="flowState">
    /// The unsealed state from the callback, carrying whatever <see cref="PrepareState"/> stored -
    /// the PKCE verifier the token request has to prove possession with.
    /// </param>
    Task<PluginOAuthTokenDto> ExchangeCodeAsync(
        Plugin plugin,
        string code,
        PluginOAuthStateDto flowState,
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

    /// <summary>
    /// Best-effort provider-side revocation for a stored access or refresh token.
    /// </summary>
    /// <remarks>
    /// Disconnecting a plugin is primarily a local account decision, so callers should not block
    /// local disconnect on provider unavailability. A successful revoke helps the next consent
    /// produce a new refresh token for providers that otherwise keep the old grant alive.
    /// </remarks>
    Task RevokeTokenAsync(
        Plugin plugin,
        string token,
        CancellationToken ct = default);
}
