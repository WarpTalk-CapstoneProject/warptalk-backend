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
    /// Success when <paramref name="connection"/> now carries a usable access token. On failure -
    /// no stored refresh token, or the provider rejecting it - the connection is persisted with
    /// status <c>expired</c> and the result carries
    /// <see cref="Domain.Constants.PluginConstants.ErrorCodes.ConnectionRequired"/>.
    /// </returns>
    Task<Result> RefreshAccessTokenAsync(
        Plugin plugin,
        PluginConnection connection,
        CancellationToken ct = default);
}
