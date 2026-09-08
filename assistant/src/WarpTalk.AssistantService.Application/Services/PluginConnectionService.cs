using System.Web;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

public class PluginConnectionService : IPluginConnectionService, IPluginTokenRefresher
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPluginProviderResolver _providerResolver;
    private readonly IPluginOAuthStateProtector _stateProtector;
    private readonly IPluginCredentialProtector _credentialProtector;
    private readonly ILogger<PluginConnectionService> _logger;
    private readonly IMcpClientProvisioner _mcpClientProvisioner;
    private readonly IWorkspacePluginGuard _workspacePluginGuard;

    public PluginConnectionService(
        IUnitOfWork unitOfWork,
        IPluginProviderResolver providerResolver,
        IPluginOAuthStateProtector stateProtector,
        IPluginCredentialProtector credentialProtector,
        ILogger<PluginConnectionService> logger,
        IMcpClientProvisioner mcpClientProvisioner,
        IWorkspacePluginGuard workspacePluginGuard)
    {
        _unitOfWork = unitOfWork;
        _providerResolver = providerResolver;
        _stateProtector = stateProtector;
        _credentialProtector = credentialProtector;
        _logger = logger;
        _mcpClientProvisioner = mcpClientProvisioner;
        _workspacePluginGuard = workspacePluginGuard;
    }

    /// <summary>
    /// The OAuth client that serves this plugin. Resolved per call rather than injected, because a
    /// single service instance handles plugins of different kinds within one request.
    /// </summary>
    private IPluginOAuthClient OAuthClientFor(Plugin plugin) =>
        _providerResolver.ResolveOAuthClient(plugin.Kind);

    public async Task<Result<PluginConnectUrlDto>> GetConnectUrlAsync(
        string pluginKey,
        Guid userId,
        Guid? workspaceId = null,
        CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginConnectUrlDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        // WT-646. This, and not the callback below, is where a connect is refused: it is the last
        // point at which nothing irreversible has happened, and the only one of the two that has a
        // workspace to judge against. Installation is already gated, so this catches the case the
        // install gate cannot - a plugin installed while the workspace still permitted plugins,
        // in a workspace that has since turned them off.
        var permitted = await _workspacePluginGuard.CanUsePluginsAsync(workspaceId, ct);
        if (!permitted.IsSuccess)
            return Result.Failure<PluginConnectUrlDto>(permitted.Error!, permitted.ErrorCode);

        var installed = await _unitOfWork.PluginInstallationRepository.AnyAsync(
            i => i.UserId == userId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct);

        if (!installed)
            return Result.Failure<PluginConnectUrlDto>("Plugin is not installed for this account.", PluginConstants.ErrorCodes.PluginNotInstalled);

        // For an MCP-backed row this is where discovery runs and the registration ladder settles
        // on a client identity, because everything the authorization URL needs - endpoints, client
        // id, negotiated auth method - comes out of it. A native row passes straight through.
        var provisioned = await _mcpClientProvisioner.ProvisionAsync(plugin, ct);
        if (!provisioned.IsSuccess)
            return Result.Failure<PluginConnectUrlDto>(provisioned.Error!, provisioned.ErrorCode);

        var scopes = PluginScopeMapper.FromJson(plugin.RequiredScopesJson);
        var oauthClient = OAuthClientFor(plugin);

        // Prepare, then seal, then build: the provider produces the secrets that must round-trip
        // (a PKCE verifier), those go inside the sealed state, and only then can a URL carrying
        // that state be assembled.
        var flowState = oauthClient.PrepareState(plugin, new PluginOAuthStateDto(userId, pluginKey));
        var state = _stateProtector.Protect(flowState);
        var url = oauthClient.BuildAuthorizationUrl(plugin, scopes, state, flowState);
        return Result.Success(new PluginConnectUrlDto(url));
    }

    public async Task<Result<PluginConnectionStatusDto>> CompleteOAuthCallbackAsync(
        string pluginKey,
        string code,
        string state,
        CancellationToken ct = default)
    {
        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess)
            return Result.Failure<PluginConnectionStatusDto>(unprotected.Error!, unprotected.ErrorCode);

        var oauthState = unprotected.Value!;

        // A per-plugin callback path carries the key twice, so the two must agree: a mismatch means
        // the state does not belong to the URL it arrived on.
        if (!string.Equals(oauthState.PluginKey, pluginKey, StringComparison.Ordinal))
            return Result.Failure<PluginConnectionStatusDto>("Invalid OAuth state.", PluginConstants.ErrorCodes.PermissionDenied);

        // Retired rows are allowed here and nowhere else. The only state that can arrive on this
        // path is one minted before the split, which names google_workspace - a row 20260907100000
        // set is_active=false. Filtering it out would refuse a consent we ourselves started while
        // the row was live. Nothing new can enter through the gap: GetConnectUrlAsync still refuses
        // an inactive row, so a retired plugin can finish a flow but can never begin one.
        return await CompleteCallbackAsync(
            oauthState,
            code,
            ct,
            route: PluginOAuthCallbackRoute.LegacyPerPlugin,
            includeRetiredPlugin: true);
    }

    /// <inheritdoc />
    public async Task<Result<PluginConnectionStatusDto>> CompleteProviderOAuthCallbackAsync(
        string provider,
        string code,
        string state,
        CancellationToken ct = default)
    {
        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess)
            return Result.Failure<PluginConnectionStatusDto>(unprotected.Error!, unprotected.ErrorCode);

        // The path names a provider, not a plugin, so the plugin key comes from the sealed state -
        // the same arrangement the MCP callback uses. What the path still contributes is a
        // cross-check: the plugin the state names has to belong to the provider whose callback
        // this is, or a state minted for one provider could be redeemed on another's.
        return await CompleteCallbackAsync(unprotected.Value!, code, ct, expectedProvider: provider);
    }

    public async Task<Result<PluginConnectionStatusDto>> CompleteMcpOAuthCallbackAsync(
        string code,
        string state,
        string? issuer = null,
        CancellationToken ct = default)
    {
        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess)
            return Result.Failure<PluginConnectionStatusDto>(unprotected.Error!, unprotected.ErrorCode);

        // No key in the path to cross-check against - the protected state is the only source, which
        // is exactly why it is integrity-protected rather than merely opaque.
        var oauthState = unprotected.Value!;

        var issuerCheck = ValidateIssuer(oauthState, issuer);
        if (!issuerCheck.IsSuccess)
            return Result.Failure<PluginConnectionStatusDto>(issuerCheck.Error!, issuerCheck.ErrorCode);

        return await CompleteCallbackAsync(oauthState, code, ct);
    }

    /// <summary>
    /// RFC 9207: an <c>iss</c> that came back must match the issuer recorded before the redirect.
    /// </summary>
    /// <remarks>
    /// Compared by simple string equality, deliberately: RFC 3986 normalisation - case folding,
    /// default-port elision, trailing slashes - is exactly what an attacker would exploit to make
    /// a different issuer compare equal.
    /// <para>
    /// An absent <c>iss</c> is allowed through, because a server that does not implement RFC 9207
    /// is common and refusing it would break every such provider. The check is a ratchet: it
    /// protects against a response claiming to be from somewhere else, not against silence.
    /// </para>
    /// </remarks>
    private static Result ValidateIssuer(PluginOAuthStateDto oauthState, string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer)) return Result.Success();
        if (string.IsNullOrWhiteSpace(oauthState.Issuer)) return Result.Success();

        return string.Equals(issuer, oauthState.Issuer, StringComparison.Ordinal)
            ? Result.Success()
            : Result.Failure(
                "The authorization response came from a different issuer than the one this flow started with.",
                PluginConstants.ErrorCodes.PermissionDenied);
    }

    /// <inheritdoc />
    public string? ReadPluginKeyFromState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;

        var unprotected = UnprotectState(state);
        return unprotected.IsSuccess ? unprotected.Value!.PluginKey : null;
    }

    /// <summary>
    /// State is attacker-reachable input: it comes back through the user's browser. Unprotecting it
    /// is the trust boundary, so failures collapse to one indistinguishable error rather than
    /// telling a prober which part it got wrong.
    /// </summary>
    private Result<PluginOAuthStateDto> UnprotectState(string state)
    {
        try
        {
            return Result.Success(_stateProtector.Unprotect(HttpUtility.UrlDecode(state)));
        }
        catch
        {
            return Result.Failure<PluginOAuthStateDto>("Invalid OAuth state.", PluginConstants.ErrorCodes.PermissionDenied);
        }
    }

    /// <param name="route">
    /// Which redirect URI the browser came back on. It has to be repeated on the token request, so
    /// this travels all the way down to the OAuth client rather than being decided there.
    /// </param>
    /// <param name="includeRetiredPlugin">
    /// Whether a catalog row with <c>is_active=false</c> may complete this callback. True only on
    /// the legacy path, where the state predates the row's retirement.
    /// </param>
    private async Task<Result<PluginConnectionStatusDto>> CompleteCallbackAsync(
        PluginOAuthStateDto oauthState,
        string code,
        CancellationToken ct,
        string? expectedProvider = null,
        PluginOAuthCallbackRoute route = PluginOAuthCallbackRoute.Configured,
        bool includeRetiredPlugin = false)
    {
        var pluginKey = oauthState.PluginKey;

        var plugin = includeRetiredPlugin
            ? await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct)
            : await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginConnectionStatusDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        // Same shape as the per-plugin route's key cross-check: when the path carries an identity
        // too, it and the state have to agree. Collapses to the same opaque error, so a prober
        // cannot tell a wrong provider from a forged state.
        if (expectedProvider != null
            && !string.Equals(plugin.Provider, expectedProvider, StringComparison.Ordinal))
            return Result.Failure<PluginConnectionStatusDto>("Invalid OAuth state.", PluginConstants.ErrorCodes.PermissionDenied);

        var installed = await _unitOfWork.PluginInstallationRepository.AnyAsync(
            i => i.UserId == oauthState.UserId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct);

        if (!installed)
            return Result.Failure<PluginConnectionStatusDto>("Plugin is not installed for this account.", PluginConstants.ErrorCodes.PluginNotInstalled);

        PluginOAuthTokenDto token;
        try
        {
            token = await OAuthClientFor(plugin).ExchangeCodeAsync(plugin, code, oauthState, route, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The caller here is a browser redirect, not an API client: whatever went wrong - a 429,
            // a 503, a redirect_uri_mismatch, an empty body - has to end as a page the user can act
            // on, and an exception would end as a raw error page instead. The provider's own words
            // go to the log, which is the only place they belong: the redirect the user follows must
            // not carry them.
            _logger.LogWarning(
                ex,
                "Exchanging the authorization code for plugin {PluginKey} failed; the user is being sent "
                    + "back to the plugins page and can try connecting again.",
                plugin.PluginKey);

            return Result.Failure<PluginConnectionStatusDto>(
                "The provider could not complete the connection. Try connecting again in a moment.",
                PluginConstants.ErrorCodes.ProviderUnavailable);
        }

        // By provider, not by plugin. A user who already consented to Google through Drive and is
        // now connecting Calendar comes back here with the same grant: this has to find that row
        // and widen it, not insert a second one that the (user_id, provider) unique constraint
        // would reject.
        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == oauthState.UserId && c.Provider == plugin.Provider, ct: ct);
        var now = DateTime.UtcNow;
        var canReuseStoredRefreshToken = connection is
        {
            Status: PluginConstants.ConnectionStatus.Connected,
            EncryptedRefreshToken: not null
        } && !string.IsNullOrWhiteSpace(connection.EncryptedRefreshToken);

        if (connection == null)
        {
            connection = new PluginConnection
            {
                Id = Guid.NewGuid(),
                UserId = oauthState.UserId,
                // Identity. NOT NULL with no database default, so omitting it here fails the
                // insert at runtime rather than at compile time.
                Provider = plugin.Provider,
                // Provenance: which catalog row sent the user to consent. Set once, on the row
                // that created the connection, and deliberately not rewritten on a later reconnect
                // through a sibling plugin - "first obtained through" is the only thing it claims.
                PluginId = plugin.Id,
                CreatedAt = now,
            };
            await _unitOfWork.PluginConnectionRepository.AddAsync(connection, ct);
        }
        else
        {
            _unitOfWork.PluginConnectionRepository.Update(connection);
        }

        connection.ProviderAccountId = token.ProviderAccountId;
        connection.ProviderEmail = token.ProviderEmail;
        connection.ScopesJson = JsonSerializer.Serialize(token.GrantedScopes);
        connection.UpdatedAt = now;

        if (string.IsNullOrWhiteSpace(token.RefreshToken) && !canReuseStoredRefreshToken)
        {
            connection.Status = PluginConstants.ConnectionStatus.Expired;
            connection.EncryptedAccessToken = null;
            connection.EncryptedRefreshToken = null;
            connection.AccessTokenExpiresAt = null;
            connection.TokenRotatedAt = null;
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success(new PluginConnectionStatusDto(
                pluginKey,
                connection.Status,
                connection.ProviderEmail,
                token.GrantedScopes));
        }

        connection.Status = PluginConstants.ConnectionStatus.Connected;
        connection.EncryptedAccessToken = _credentialProtector.Protect(token.AccessToken);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            connection.EncryptedRefreshToken = _credentialProtector.Protect(token.RefreshToken);
        connection.AccessTokenExpiresAt = token.AccessTokenExpiresAt;
        connection.TokenRotatedAt = now;

        await SyncToolManifestAsync(plugin, connection, now, ct);

        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new PluginConnectionStatusDto(
            pluginKey,
            connection.Status,
            connection.ProviderEmail,
            token.GrantedScopes));
    }

    /// <summary>
    /// Refreshes the cached tool set for an MCP-backed row, using the connection just established.
    /// </summary>
    /// <remarks>
    /// This is the step that turns a catalog row into working tools: <c>tools_json</c> is authored
    /// by us for a native row but is a cache of <c>tools/list</c> for an MCP one, and nothing else
    /// populates it. Until it runs, a connected plugin shows zero tools.
    /// <para>
    /// A failure here does not fail the connect. The grant is real and stored; the tool list is
    /// recoverable by reconnecting, and throwing away a working connection over a momentarily
    /// unreachable server would be a much worse trade.
    /// </para>
    /// <para>
    /// Deliberately not gated by workspace policy, and this stayed true through WT-646, which
    /// moved the connect-time gate up to <see cref="GetConnectUrlAsync"/>. By the time control
    /// reaches here the user has already consented at the provider and the grant exists; refusing
    /// it would strand a real consent rather than prevent one. There is also no workspace to judge
    /// against - a callback is a browser redirect from the provider and carries no workspace
    /// context, only the protected state. Workspace policy is enforced where a user is actually in
    /// a workspace: the catalog and install paths in <c>PluginInstallationService</c>, the
    /// connect-url path above, and the list and execute paths in <c>McpToolOrchestrator</c>.
    /// </para>
    /// </remarks>
    private async Task SyncToolManifestAsync(
        Plugin plugin,
        PluginConnection connection,
        DateTime now,
        CancellationToken ct)
    {
        if (!string.Equals(plugin.Kind, PluginConstants.PluginKind.Mcp, StringComparison.Ordinal)) return;

        try
        {
            var definition = PluginDefinitionMapper.ToDefinition(plugin);
            var tools = await _providerResolver.ResolveGateway(plugin.Kind)
                .ListToolsAsync(definition, connection, ct);

            plugin.ToolsJson = JsonSerializer.Serialize(tools);
            plugin.ToolsSyncedAt = now;
            plugin.UpdatedAt = now;
            _unitOfWork.PluginRepository.Update(plugin);
        }
        catch (Exception e)
        {
            _logger.LogWarning(
                e,
                "Could not refresh the tool manifest for plugin {PluginKey}; the connection stands and "
                    + "the tool list will refresh on the next reconnect.",
                plugin.PluginKey);
        }
    }

    public async Task<Result<PluginConnectionStatusDto>> GetStatusAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginConnectionStatusDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        // The grant belongs to the provider, so all three Google plugins report the same connection
        // - which is the point: a user who consented through Drive is connected for Calendar too,
        // and looking this up by plugin id would tell them otherwise.
        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == plugin.Provider, ct: ct);

        if (connection == null)
            return Result.Success(new PluginConnectionStatusDto(pluginKey, PluginConstants.ConnectionStatus.NotConnected, null, Array.Empty<string>()));

        return Result.Success(new PluginConnectionStatusDto(
            pluginKey,
            connection.Status,
            connection.ProviderEmail,
            PluginScopeMapper.FromJson(connection.ScopesJson)));
    }

    public async Task<Result> RefreshAccessTokenAsync(
        Plugin plugin,
        PluginConnection connection,
        CancellationToken ct = default)
    {
        // Nothing to refresh with. Permanent by construction - only a new consent stores one.
        if (string.IsNullOrWhiteSpace(connection.EncryptedRefreshToken))
            return await PluginConnectionStateTransitions.MarkExpiredAsync(_unitOfWork, connection, ct);

        string refreshToken;
        try
        {
            refreshToken = _credentialProtector.Unprotect(connection.EncryptedRefreshToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Undecryptable stored material (rotated Data Protection key ring) is as dead as a
            // revoked grant - the only way out is a fresh consent.
            return await PluginConnectionStateTransitions.MarkExpiredAsync(_unitOfWork, connection, ct);
        }

        PluginOAuthRefreshResultDto refresh;
        try
        {
            refresh = await OAuthClientFor(plugin).RefreshAccessTokenAsync(plugin, refreshToken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The client classifies everything it can foresee; an unforeseen fault (a provider
            // contract change, a bug in the client) is not evidence the grant is dead. Ending the
            // connection is the one outcome the user cannot undo without a browser round trip, so
            // an unknown fault degrades to transient rather than to destructive.
            return PluginConnectionRefreshFailures.Transient(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                "The provider could not be reached to refresh access. Try again in a moment.");
        }

        switch (refresh.Outcome)
        {
            case PluginOAuthRefreshOutcome.GrantRejected:
                return await PluginConnectionStateTransitions.MarkExpiredAsync(_unitOfWork, connection, ct);

            case PluginOAuthRefreshOutcome.ProviderRateLimited:
                return PluginConnectionRefreshFailures.Transient(
                    PluginConstants.ErrorCodes.ProviderRateLimited,
                    "The provider is rate limiting this account. Try again in a moment.");

            case PluginOAuthRefreshOutcome.ProviderUnavailable:
                return PluginConnectionRefreshFailures.Transient(
                    PluginConstants.ErrorCodes.ProviderUnavailable,
                    "The provider could not be reached to refresh access. Try again in a moment.");
        }

        var token = refresh.Token;
        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
            return PluginConnectionRefreshFailures.Transient(
                PluginConstants.ErrorCodes.ProviderUnavailable,
                "The provider could not be reached to refresh access. Try again in a moment.");

        var now = DateTime.UtcNow;
        connection.EncryptedAccessToken = _credentialProtector.Protect(token.AccessToken);
        // Google returns a refresh token only on the first consent, so an omitted one means "keep
        // using the stored one", not "the grant lost its refresh token".
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            connection.EncryptedRefreshToken = _credentialProtector.Protect(token.RefreshToken);
        connection.AccessTokenExpiresAt = token.AccessTokenExpiresAt;
        connection.Status = PluginConstants.ConnectionStatus.Connected;
        connection.TokenRotatedAt = now;
        connection.UpdatedAt = now;
        _unitOfWork.PluginConnectionRepository.Update(connection);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> DisconnectAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct);
        if (plugin == null)
            return Result.Failure("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        // Disconnecting is per-provider, and that is a real behaviour change worth being explicit
        // about: disconnecting Drive ends the Google grant, so Calendar and Meet go with it.
        // Google revokes per grant, not per token, so the alternative - dropping only "the Drive
        // connection" - would leave two rows we believe are healthy pointing at a revoked grant.
        // Disconnecting one product and keeping the others would need an incremental de-scope,
        // which Google's revoke endpoint does not offer.
        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == plugin.Provider, ct: ct);

        if (connection == null)
            return Result.Success();

        await TryRevokeProviderTokenAsync(plugin, connection, ct);

        connection.Status = PluginConstants.ConnectionStatus.Revoked;
        connection.EncryptedAccessToken = null;
        connection.EncryptedRefreshToken = null;
        connection.AccessTokenExpiresAt = null;
        connection.TokenRotatedAt = null;
        connection.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.PluginConnectionRepository.Update(connection);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task TryRevokeProviderTokenAsync(
        Plugin plugin,
        PluginConnection connection,
        CancellationToken ct)
    {
        var encryptedToken =
            string.IsNullOrWhiteSpace(connection.EncryptedRefreshToken)
                ? connection.EncryptedAccessToken
                : connection.EncryptedRefreshToken;
        if (string.IsNullOrWhiteSpace(encryptedToken))
            return;

        string token;
        try
        {
            token = _credentialProtector.Unprotect(encryptedToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        try
        {
            await OAuthClientFor(plugin).RevokeTokenAsync(plugin, token, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Local disconnect is still authoritative for WarpTalk. Provider revoke is best-effort
            // because a network failure here should not trap the user in a connected local state.
        }
    }
}
