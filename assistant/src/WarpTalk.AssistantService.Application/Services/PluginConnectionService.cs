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

    public PluginConnectionService(
        IUnitOfWork unitOfWork,
        IPluginProviderResolver providerResolver,
        IPluginOAuthStateProtector stateProtector,
        IPluginCredentialProtector credentialProtector,
        ILogger<PluginConnectionService> logger,
        IMcpClientProvisioner mcpClientProvisioner)
    {
        _unitOfWork = unitOfWork;
        _providerResolver = providerResolver;
        _stateProtector = stateProtector;
        _credentialProtector = credentialProtector;
        _logger = logger;
        _mcpClientProvisioner = mcpClientProvisioner;
    }

    /// <summary>
    /// The OAuth client that serves this plugin. Resolved per call rather than injected, because a
    /// single service instance handles plugins of different kinds within one request.
    /// </summary>
    private IPluginOAuthClient OAuthClientFor(Plugin plugin) =>
        _providerResolver.ResolveOAuthClient(plugin.Kind);

    public async Task<Result<PluginConnectUrlDto>> GetConnectUrlAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginConnectUrlDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

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

        return await CompleteCallbackAsync(oauthState, code, ct);
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

    private async Task<Result<PluginConnectionStatusDto>> CompleteCallbackAsync(
        PluginOAuthStateDto oauthState,
        string code,
        CancellationToken ct)
    {
        var pluginKey = oauthState.PluginKey;

        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey && p.IsActive, ct: ct);
        if (plugin == null)
            return Result.Failure<PluginConnectionStatusDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        var installed = await _unitOfWork.PluginInstallationRepository.AnyAsync(
            i => i.UserId == oauthState.UserId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct);

        if (!installed)
            return Result.Failure<PluginConnectionStatusDto>("Plugin is not installed for this account.", PluginConstants.ErrorCodes.PluginNotInstalled);

        var token = await OAuthClientFor(plugin).ExchangeCodeAsync(plugin, code, oauthState, ct);
        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == oauthState.UserId && c.PluginId == plugin.Id, ct: ct);
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
    /// Deliberately not gated by workspace policy - connecting is personal and workspace-independent.
    /// <c>AllowAnyPlugins</c> is enforced where a user is actually in a workspace, on both the list
    /// and execute paths in <c>McpToolOrchestrator</c>.
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

        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.PluginId == plugin.Id, ct: ct);

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

        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.PluginId == plugin.Id, ct: ct);

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
