using System.Web;
using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

public class PluginConnectionService : IPluginConnectionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPluginOAuthClient _oauthClient;
    private readonly IPluginOAuthStateProtector _stateProtector;
    private readonly IPluginCredentialProtector _credentialProtector;

    public PluginConnectionService(
        IUnitOfWork unitOfWork,
        IPluginOAuthClient oauthClient,
        IPluginOAuthStateProtector stateProtector,
        IPluginCredentialProtector credentialProtector)
    {
        _unitOfWork = unitOfWork;
        _oauthClient = oauthClient;
        _stateProtector = stateProtector;
        _credentialProtector = credentialProtector;
    }

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
        var url = _oauthClient.BuildAuthorizationUrl(plugin, scopes, state);
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

        var token = await _oauthClient.ExchangeCodeAsync(plugin, code, ct);
        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == oauthState.UserId && c.PluginId == plugin.Id, ct: ct);
        var now = DateTime.UtcNow;

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
        connection.Status = PluginConstants.ConnectionStatus.Connected;
        connection.ScopesJson = JsonSerializer.Serialize(token.GrantedScopes);
        connection.EncryptedAccessToken = _credentialProtector.Protect(token.AccessToken);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            connection.EncryptedRefreshToken = _credentialProtector.Protect(token.RefreshToken);
        connection.AccessTokenExpiresAt = token.AccessTokenExpiresAt;
        connection.TokenRotatedAt = now;
        connection.UpdatedAt = now;

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

    public async Task<Result> DisconnectAsync(string pluginKey, Guid userId, CancellationToken ct = default)
    {
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(p => p.PluginKey == pluginKey, ct: ct);
        if (plugin == null)
            return Result.Failure("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);

        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.PluginId == plugin.Id, ct: ct);

        if (connection == null)
            return Result.Success();

        connection.Status = PluginConstants.ConnectionStatus.Revoked;
        connection.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.PluginConnectionRepository.Update(connection);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
