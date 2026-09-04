using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginConnectionService
{
    Task<Result<PluginConnectUrlDto>> GetConnectUrlAsync(string pluginKey, Guid userId, CancellationToken ct = default);
    Task<Result<PluginConnectionStatusDto>> CompleteOAuthCallbackAsync(string pluginKey, string code, string state, CancellationToken ct = default);

    /// <summary>
    /// Completes the callback for a <c>kind='mcp'</c> plugin, which arrives on one fixed redirect
    /// URI shared by every MCP plugin rather than a per-plugin path.
    /// </summary>
    /// <remarks>
    /// The plugin key therefore comes from the protected <c>state</c> and nowhere else. That is the
    /// point of the fixed URI: a Client ID Metadata Document has to enumerate its redirect URIs and
    /// the authorization server matches them exactly, so a per-plugin path would mean re-publishing
    /// that document every time a catalog row is added - and the document is cached by servers for
    /// as long as a week.
    /// </remarks>
    /// <param name="issuer">
    /// The RFC 9207 <c>iss</c> from the authorization response, when the server sent one. It is
    /// compared against the issuer recorded before the redirect, which is what closes
    /// authorization-server mix-up.
    /// </param>
    Task<Result<PluginConnectionStatusDto>> CompleteMcpOAuthCallbackAsync(
        string code,
        string state,
        string? issuer = null,
        CancellationToken ct = default);
    Task<Result<PluginConnectionStatusDto>> GetStatusAsync(string pluginKey, Guid userId, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string pluginKey, Guid userId, CancellationToken ct = default);
}
