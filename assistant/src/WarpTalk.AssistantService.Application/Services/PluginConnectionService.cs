using System.Web;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Exceptions;
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
        string? client = null,
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
        var flowState = oauthClient.PrepareState(
            plugin,
            new PluginOAuthStateDto(userId, pluginKey, Client: PluginConstants.OAuthClient.Normalize(client)));
        var state = _stateProtector.Protect(flowState);
        var url = oauthClient.BuildAuthorizationUrl(plugin, scopes, state, flowState);
        return Result.Success(new PluginConnectUrlDto(url));
    }

    public async Task<PluginOAuthCallbackOutcomeDto> CompleteOAuthCallbackAsync(
        string pluginKey,
        string code,
        string state,
        CancellationToken ct = default)
    {
        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess)
            return Failed(unprotected.ErrorCode!);

        var oauthState = unprotected.Value!;

        // A per-plugin callback path carries the key twice, so the two must agree: a mismatch means
        // the state does not belong to the URL it arrived on.
        if (!string.Equals(oauthState.PluginKey, pluginKey, StringComparison.Ordinal))
            return Failed(PluginConstants.ErrorCodes.PermissionDenied, oauthState.Client);

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
    public async Task<PluginOAuthCallbackOutcomeDto> CompleteProviderOAuthCallbackAsync(
        string provider,
        string code,
        string state,
        CancellationToken ct = default)
    {
        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess)
            return Failed(unprotected.ErrorCode!);

        // The path names a provider, not a plugin, so the plugin key comes from the sealed state -
        // the same arrangement the MCP callback uses. What the path still contributes is a
        // cross-check: the plugin the state names has to belong to the provider whose callback
        // this is, or a state minted for one provider could be redeemed on another's.
        return await CompleteCallbackAsync(unprotected.Value!, code, ct, expectedProvider: provider);
    }

    public async Task<PluginOAuthCallbackOutcomeDto> CompleteMcpOAuthCallbackAsync(
        string code,
        string state,
        string? issuer = null,
        CancellationToken ct = default)
    {
        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess)
            return Failed(unprotected.ErrorCode!);

        // No key in the path to cross-check against - the protected state is the only source, which
        // is exactly why it is integrity-protected rather than merely opaque.
        var oauthState = unprotected.Value!;

        var issuerCheck = ValidateIssuer(oauthState, issuer);
        if (!issuerCheck.IsSuccess)
            return Failed(issuerCheck.ErrorCode!, oauthState.Client);

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
    public PluginOAuthFlowHintDto? ReadFlowHint(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;

        var unprotected = UnprotectState(state);
        if (!unprotected.IsSuccess) return null;

        var oauthState = unprotected.Value!;
        return new PluginOAuthFlowHintDto(
            oauthState.PluginKey,
            PluginConstants.OAuthClient.Normalize(oauthState.Client));
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

    /// <summary>
    /// The shared tail of every callback: redeem the code, store the grant, and say what happened.
    /// </summary>
    /// <remarks>
    /// Everything runs inside one <c>catch</c>. The exchange calls a provider over the network with
    /// credentials read from configuration, and the write that follows touches the database - so an
    /// empty client secret, a provider outage and a failed save all end here. Before this, each of
    /// them escaped as an exception and reached the user as a JSON error page on the API domain,
    /// because a callback's caller is a browser following a redirect and has nowhere to put one.
    /// <para>
    /// The provider's own words go to the log and nowhere else: whatever went wrong - a 429, a 503,
    /// a redirect_uri_mismatch, an empty body - has to reach the user as a page they can act on,
    /// and the redirect they follow must not carry a provider's error text.
    /// </para>
    /// </remarks>
    /// <param name="route">
    /// Which redirect URI the browser came back on. It has to be repeated on the token request, so
    /// this travels all the way down to the OAuth client rather than being decided there.
    /// </param>
    /// <param name="includeRetiredPlugin">
    /// Whether a catalog row with <c>is_active=false</c> may complete this callback. True only on
    /// the legacy path, where the state predates the row's retirement.
    /// </param>
    private async Task<PluginOAuthCallbackOutcomeDto> CompleteCallbackAsync(
        PluginOAuthStateDto oauthState,
        string code,
        CancellationToken ct,
        string? expectedProvider = null,
        PluginOAuthCallbackRoute route = PluginOAuthCallbackRoute.Configured,
        bool includeRetiredPlugin = false)
    {
        var pluginKey = oauthState.PluginKey;
        var client = PluginConstants.OAuthClient.Normalize(oauthState.Client);
        string? provider = null;

        try
        {
            var plugin = includeRetiredPlugin
                ? await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct)
                : await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
            if (plugin == null)
                return Failed(PluginConstants.ErrorCodes.UnknownPlugin, client, pluginKey: pluginKey);

            provider = plugin.Provider;

            // Same shape as the per-plugin route's key cross-check: when the path carries an
            // identity too, it and the state have to agree. Collapses to the same opaque error, so
            // a prober cannot tell a wrong provider from a forged state.
            if (expectedProvider != null
                && !string.Equals(plugin.Provider, expectedProvider, StringComparison.Ordinal))
                return Failed(PluginConstants.ErrorCodes.PermissionDenied, client, provider, pluginKey);

            var installed = await _unitOfWork.PluginInstallationRepository.AnyAsync(
                i => i.UserId == oauthState.UserId
                    && i.PluginId == plugin.Id
                    && i.Status == PluginConstants.InstallationStatus.Installed,
                ct);

            if (!installed)
                return Failed(PluginConstants.ErrorCodes.PluginNotInstalled, client, provider, pluginKey);

            var token = await OAuthClientFor(plugin).ExchangeCodeAsync(plugin, code, oauthState, route, ct);

            // By provider, not by plugin. A user who already consented to Google through Drive and
            // is now connecting Calendar comes back here with the same grant: this has to find that
            // row and widen it, not insert a second one that the (user_id, provider) unique
            // constraint would reject.
            var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
                c => c.UserId == oauthState.UserId && c.Provider == plugin.Provider, ct: ct);

            // One grant backs every plugin of this provider, so consenting with a different account
            // does not add a connection - it overwrites the one Drive, Calendar and Meet are all
            // reading. Left unchecked that is silent: every tile changes email, the previous refresh
            // token is destroyed here but stays live at the provider because nothing revokes it, and
            // from then on the same tools read a different Drive than the user's history implies.
            //
            // Refused rather than merged. The user has to disconnect deliberately, which is also the
            // only path that revokes the grant being replaced.
            if (connection is { ProviderAccountId: not null }
                && !string.IsNullOrWhiteSpace(token.ProviderAccountId)
                && !string.Equals(connection.ProviderAccountId, token.ProviderAccountId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Refused a {Provider} consent for a different account on an existing connection "
                        + "(plugin {PluginKey}, user {UserId}).",
                    plugin.Provider,
                    pluginKey,
                    oauthState.UserId);

                // The refusal comes after the exchange, because the account is only knowable from
                // the token response - so a real grant now exists at the provider for an account we
                // are about to forget. Dropping it would leave the user's second account holding
                // access to WarpTalk that WarpTalk has no record of and no later way to revoke.
                await TryRevokeIssuedTokenAsync(plugin, token, ct);

                return Failed(
                    PluginConstants.ErrorCodes.ProviderAccountMismatch,
                    client,
                    provider,
                    pluginKey);
            }

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
                    // that created the connection, and deliberately not rewritten on a later
                    // reconnect through a sibling plugin - "first obtained through" is the only
                    // thing it claims.
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

                // A consent that came back without a refresh token leaves nothing to act with
                // later. The row is honest about that (`expired`), and the user is told to connect
                // again rather than shown a success they cannot use.
                return new PluginOAuthCallbackOutcomeDto(
                    PluginConstants.CallbackStatus.Error,
                    PluginConstants.ErrorCodes.ConnectionRequired,
                    provider,
                    pluginKey,
                    client,
                    new PluginConnectionStatusDto(
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

            return new PluginOAuthCallbackOutcomeDto(
                await ScopeOutcomeAsync(plugin, oauthState.UserId, token.GrantedScopes, ct),
                null,
                provider,
                pluginKey,
                client,
                new PluginConnectionStatusDto(
                    pluginKey,
                    connection.Status,
                    connection.ProviderEmail,
                    token.GrantedScopes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = Classify(ex);
            _logger.LogError(
                ex,
                "Plugin OAuth callback could not be completed for {PluginKey} ({Reason}); the user is "
                    + "being sent back to the plugins page.",
                pluginKey,
                reason);
            return Failed(reason, client, provider, pluginKey);
        }
    }

    /// <summary>
    /// Connected, or connected-but-narrower than the plugin asked for.
    /// </summary>
    /// <remarks>
    /// The same subset test the plugins page uses to decide whether a tile counts as connected, so
    /// the redirect and the tile cannot disagree about the grant the user just gave.
    /// </remarks>
    private async Task<string> ScopeOutcomeAsync(
        Plugin plugin,
        Guid userId,
        IReadOnlyList<string> grantedScopes,
        CancellationToken ct)
    {
        var granted = new HashSet<string>(grantedScopes, StringComparer.Ordinal);

        if (!Satisfies(plugin, granted)) return PluginConstants.CallbackStatus.Partial;

        // Asked of every installed plugin on this provider, not only the one the user clicked.
        //
        // The grant is shared and this callback replaces its whole scope set, so consenting through
        // Calendar decides what Drive can do. The provider is asked for the union - Google is sent
        // include_granted_scopes=true - but the user can still clear a previously granted box on
        // the consent screen, and then the narrower set is what comes back and what gets stored.
        //
        // Judging that against the clicked plugin alone reported `connected` for a consent that had
        // just dropped Drive's scope: the Drive tile went on saying Connected, and the user found
        // out when a tool call failed in the middle of an answer.
        //
        // Deliberately NOT solved by unioning the new scopes with the stored ones. The stored set
        // has to describe what the provider will actually honour; widening it here would put the
        // failure back one step, at the provider, with the scope gate saying yes on the way past.
        var installations = await _unitOfWork.PluginInstallationRepository.FindAsync(
            i => i.UserId == userId && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);
        var installedPluginIds = installations.Select(i => i.PluginId).ToHashSet();

        var siblings = await _unitOfWork.PluginRepository.FindAsync(
            p => installedPluginIds.Contains(p.Id)
                && p.Provider == plugin.Provider
                && p.IsActive,
            ct: ct);

        return siblings.All(sibling => Satisfies(sibling, granted))
            ? PluginConstants.CallbackStatus.Connected
            : PluginConstants.CallbackStatus.Partial;
    }

    private static bool Satisfies(Plugin plugin, HashSet<string> grantedScopes) =>
        PluginScopeMapper.FromJson(plugin.RequiredScopesJson).All(grantedScopes.Contains);

    /// <summary>
    /// Which of the three sentences the user should read.
    /// </summary>
    /// <remarks>
    /// Only <see cref="PluginNotConfiguredException"/> earns the configuration reason, and it exists
    /// for exactly this test. Classifying on <c>InvalidOperationException</c> would have caught the
    /// OAuth clients' own "Google refused the code" as a configuration fault, because that is the
    /// type they throw too - and then told an operator to go set a variable that was already set.
    /// <para>
    /// Everything else degrades to transient. Being wrong that way costs the user a retry; being
    /// wrong the other way tells them to retry something that will fail identically forever.
    /// </para>
    /// </remarks>
    private static string Classify(Exception ex) => ex switch
    {
        PluginNotConfiguredException => PluginConstants.ErrorCodes.ProviderConfiguration,
        _ => PluginConstants.ErrorCodes.ProviderUnavailable,
    };

    private static PluginOAuthCallbackOutcomeDto Failed(
        string reason,
        string? client = null,
        string? provider = null,
        string? pluginKey = null) =>
        new(
            PluginConstants.CallbackStatus.Error,
            reason,
            provider,
            pluginKey,
            PluginConstants.OAuthClient.Normalize(client),
            null);

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

    /// <summary>
    /// Best-effort revocation of a grant we obtained and then declined to keep.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryRevokeProviderTokenAsync"/>, which reads the encrypted tokens off
    /// a stored connection: there is no connection here and there never will be one. The refresh
    /// token is preferred for the same reason it is there - providers revoke the whole grant from
    /// it, where an access token may only drop itself.
    /// <para>
    /// Swallows everything. The user is already being redirected to an error page that tells them
    /// what to do; a provider having a bad minute must not turn that into a 500, and the grant left
    /// behind is the same one they would have been left with before this call existed.
    /// </para>
    /// </remarks>
    private async Task TryRevokeIssuedTokenAsync(
        Plugin plugin,
        PluginOAuthTokenDto token,
        CancellationToken ct)
    {
        var revocable = string.IsNullOrWhiteSpace(token.RefreshToken)
            ? token.AccessToken
            : token.RefreshToken;
        if (string.IsNullOrWhiteSpace(revocable)) return;

        try
        {
            await OAuthClientFor(plugin).RevokeTokenAsync(plugin, revocable, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not revoke the grant from a refused {Provider} consent for plugin {PluginKey}.",
                plugin.Provider,
                plugin.PluginKey);
        }
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
