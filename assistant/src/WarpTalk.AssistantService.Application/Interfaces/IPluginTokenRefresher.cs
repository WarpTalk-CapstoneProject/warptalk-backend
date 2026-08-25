using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The one slice of the connection lifecycle that MCP execution needs: swap an expired provider
/// access token for a fresh one and persist it.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="IPluginConnectionService"/> (which
/// <c>PluginConnectionService</c> also implements) so <c>McpToolOrchestrator</c> does not take a
/// dependency on the connect-url / OAuth-callback / disconnect surface it never calls.
/// </remarks>
public interface IPluginTokenRefresher
{
    /// <summary>
    /// Exchanges the stored refresh token for a new access token, re-encrypts it onto
    /// <paramref name="connection"/> and saves it.
    /// </summary>
    /// <returns>
    /// Success when <paramref name="connection"/> now carries a usable access token. On failure the
    /// <see cref="Result.ErrorCode"/> says which kind of failure it was, and the caller is expected
    /// to pass it straight through rather than flattening it:
    /// <list type="bullet">
    /// <item>
    /// <see cref="Domain.Constants.PluginConstants.ErrorCodes.ConnectionRequired"/> - the grant is
    /// gone for good (provider rejected the refresh token, nothing stored to refresh with, or
    /// stored material that no longer decrypts). The connection is persisted as <c>expired</c> and
    /// only a fresh consent brings it back.
    /// </item>
    /// <item>
    /// <see cref="Domain.Constants.PluginConstants.ErrorCodes.ProviderUnavailable"/> or
    /// <see cref="Domain.Constants.PluginConstants.ErrorCodes.ProviderRateLimited"/> - the provider
    /// or the network got in the way. <c>plugin_connections.status</c> is left untouched, so the
    /// very next call can simply try again.
    /// </item>
    /// </list>
    /// </returns>
    Task<Result> RefreshAccessTokenAsync(
        Plugin plugin,
        PluginConnection connection,
        CancellationToken ct = default);
}
