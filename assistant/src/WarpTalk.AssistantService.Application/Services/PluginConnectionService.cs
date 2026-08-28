using System.Web;
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

    public PluginConnectionService(
        IUnitOfWork unitOfWork,
        IPluginProviderResolver providerResolver,
        IPluginOAuthStateProtector stateProtector,
        IPluginCredentialProtector credentialProtector)
    {
        _unitOfWork = unitOfWork;
        _providerResolver = providerResolver;
        _stateProtector = stateProtector;
        _credentialProtector = credentialProtector;
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

        var scopes = PluginScopeMapper.FromJson(plugin.RequiredScopesJson);
        var state = _stateProtector.Protect(new PluginOAuthStateDto(userId, pluginKey));
        var url = OAuthClientFor(plugin).BuildAuthorizationUrl(plugin, scopes, state);
        return Result.Success(new PluginConnectUrlDto(url));
    }

    public async Task<Result<PluginConnectionStatusDto>> CompleteOAuthCallbackAsync(
        string pluginKey,
        string code,
        string state,
        CancellationToken ct = default)
    {
        PluginOAuthStateDto oauthState;
        try
        {
            oauthState = _stateProtector.Unprotect(HttpUtility.UrlDecode(state));
        }
        catch
        {
            return Result.Failure<PluginConnectionStatusDto>("Invalid OAuth state.", PluginConstants.ErrorCodes.PermissionDenied);
        }

        if (!string.Equals(oauthState.PluginKey, pluginKey, StringComparison.Ordinal))
            return Result.Failure<PluginConnectionStatusDto>("Invalid OAuth state.", PluginConstants.ErrorCodes.PermissionDenied);

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

        var token = await OAuthClientFor(plugin).ExchangeCodeAsync(plugin, code, ct);
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

        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new PluginConnectionStatusDto(
            pluginKey,
            connection.Status,
            connection.ProviderEmail,
            token.GrantedScopes));
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
