using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginConnectionService
{
    /// <param name="client">
    /// Which surface is asking - see <see cref="Domain.Constants.PluginConstants.OAuthClient"/>.
    /// It is sealed into the state here so the callback can tell, several minutes and one external
    /// browser later, whether it has to hand the user back to the desktop app.
    /// </param>
    /// <param name="workspaceId">
    /// The workspace the connect is being started from, when there is one. WT-646: a plugin the
    /// workspace's policy excludes is refused here rather than at the callback, which has no
    /// workspace context and arrives after the user has already consented. Null - the default -
    /// skips the gate, so a connect made outside any workspace behaves as it did before.
    /// </param>
    Task<Result<PluginConnectUrlDto>> GetConnectUrlAsync(
        string pluginKey,
        Guid userId,
        string? client = null,
        Guid? workspaceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Completes a callback that arrived on the legacy per-plugin path,
    /// <c>{pluginKey}/oauth/callback</c>.
    /// </summary>
    /// <remarks>
    /// Superseded by <see cref="CompleteProviderOAuthCallbackAsync"/> and kept for one narrow case:
    /// a consent that was authorized against the old per-plugin redirect URI before the
    /// provider-scoped one shipped, and comes back after it. Two things follow from that, and both
    /// are implemented rather than assumed. The token exchange repeats the old URI, because the
    /// provider matches it against the URI the authorization request carried - so the old URI has
    /// to remain registered with the provider for as long as this path is live. And the plugin the
    /// state names may be retired: the only key that can arrive here is <c>google_workspace</c>,
    /// which the split deactivated, so the lookup on this path does not filter on <c>is_active</c>.
    /// <para>
    /// A retired row can therefore finish a flow but never start one - <see cref="GetConnectUrlAsync"/>
    /// still refuses it. When the provider's console entry for the old URI goes away, this method,
    /// its route, and the option that holds the old URI go with it.
    /// </para>
    /// </remarks>
    Task<PluginOAuthCallbackOutcomeDto> CompleteOAuthCallbackAsync(
        string pluginKey,
        string code,
        string state,
        CancellationToken ct = default);

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
    Task<PluginOAuthCallbackOutcomeDto> CompleteProviderOAuthCallbackAsync(
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
    Task<PluginOAuthCallbackOutcomeDto> CompleteMcpOAuthCallbackAsync(
        string code,
        string state,
        string? issuer = null,
        CancellationToken ct = default);

    /// <summary>
    /// The little a caller outside this service may learn from a sealed OAuth state, or
    /// <c>null</c> when the state is missing or cannot be read.
    /// </summary>
    /// <remarks>
    /// A callback that never got as far as an exchange has no outcome to take a plugin key from,
    /// and the user still has to land back on the tile they started from - on the surface they
    /// started from. The provider-scoped and MCP callbacks have no key in their path, so the state
    /// is the only place left to ask, including when the provider came back with
    /// <c>error=access_denied</c> and no code at all, which it does return the state with.
    /// <c>null</c> is the answer that says nothing is recoverable, and the caller reports that as
    /// an invalid state rather than guessing.
    /// </remarks>
    PluginOAuthFlowHintDto? ReadFlowHint(string? state);

    Task<Result<PluginConnectionStatusDto>> GetStatusAsync(string pluginKey, Guid userId, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string pluginKey, Guid userId, CancellationToken ct = default);
}
