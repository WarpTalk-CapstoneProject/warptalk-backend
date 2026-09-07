using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginConnectionService
{
    Task<Result<PluginConnectUrlDto>> GetConnectUrlAsync(string pluginKey, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Completes a callback that arrived on the legacy per-plugin path,
    /// <c>{pluginKey}/oauth/callback</c>.
    /// </summary>
    /// <remarks>
    /// Superseded by <see cref="CompleteProviderOAuthCallbackAsync"/> and kept because live grants
    /// and deployed Google Cloud Console configuration still point at the old path. A flow that
    /// started before the provider-scoped redirect URI shipped comes back here.
    /// </remarks>
    Task<Result<PluginConnectionStatusDto>> CompleteOAuthCallbackAsync(string pluginKey, string code, string state, CancellationToken ct = default);

    /// <summary>
    /// Completes a callback for a <c>native</c> plugin on the provider-scoped redirect URI,
    /// <c>oauth/{provider}/callback</c>, shared by every catalog row of that provider.
    /// </summary>
    /// <remarks>
    /// google_drive, google_calendar and google_meet are three rows against one Google OAuth
    /// client. A per-plugin redirect URI would mean three entries in Google Cloud Console and a
    /// console change every time a Google product is added; one provider-scoped URI is registered
    /// once.
    /// <para>
    /// The plugin key therefore comes from the protected <c>state</c> and nowhere else - the same
    /// arrangement <see cref="CompleteMcpOAuthCallbackAsync"/> already uses, and the reason
    /// <c>state</c> is integrity-protected rather than merely opaque.
    /// </para>
    /// </remarks>
    /// <param name="provider">
    /// The provider segment of the path the response arrived on. Cross-checked against the
    /// provider of the plugin named in <c>state</c>, so a state minted for one provider cannot be
    /// redeemed on another's callback.
    /// </param>
    Task<Result<PluginConnectionStatusDto>> CompleteProviderOAuthCallbackAsync(
        string provider,
        string code,
        string state,
        CancellationToken ct = default);

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
