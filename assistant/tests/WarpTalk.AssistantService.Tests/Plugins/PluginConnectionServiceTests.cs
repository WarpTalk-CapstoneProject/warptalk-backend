using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

public class PluginConnectionServiceTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PluginId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginRepository _pluginRepository = Substitute.For<IPluginRepository>();
    private readonly IPluginInstallationRepository _installationRepository = Substitute.For<IPluginInstallationRepository>();
    private readonly IPluginConnectionRepository _connectionRepository = Substitute.For<IPluginConnectionRepository>();
    private readonly IPluginOAuthClient _oauthClient = Substitute.For<IPluginOAuthClient>();
    private readonly IPluginOAuthStateProtector _stateProtector = Substitute.For<IPluginOAuthStateProtector>();
    private readonly IPluginCredentialProtector _credentialProtector = Substitute.For<IPluginCredentialProtector>();

    public PluginConnectionServiceTests()
    {
        _unitOfWork.PluginRepository.Returns(_pluginRepository);
        _unitOfWork.PluginInstallationRepository.Returns(_installationRepository);
        _unitOfWork.PluginConnectionRepository.Returns(_connectionRepository);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);
        // A provider that carries nothing extra through the round trip returns the state unchanged;
        // without this the substitute hands back null and the state never matches.
        _oauthClient.PrepareState(Arg.Any<Plugin>(), Arg.Any<PluginOAuthStateDto>())
            .Returns(call => call.Arg<PluginOAuthStateDto>());
        _credentialProtector.Protect(Arg.Any<string>()).Returns(call => $"protected:{call.Arg<string>()}");
        _credentialProtector.Unprotect(Arg.Any<string>())
            .Returns(call => call.Arg<string>().Replace("protected:", "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetConnectUrlAsync_ReturnsPluginNotInstalled_WhenAccountDidNotInstallPlugin()
    {
        var plugin = GoogleWorkspacePlugin();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.AnyAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateSut().GetConnectUrlAsync(PluginConstants.GoogleWorkspace, UserId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PluginNotInstalled, result.ErrorCode);
        _oauthClient.DidNotReceive()
            .BuildAuthorizationUrl(
                Arg.Any<Plugin>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(),
                Arg.Any<PluginOAuthStateDto>());
    }

    [Fact]
    public async Task GetConnectUrlAsync_ReturnsProviderAuthorizationUrl_WhenAccountInstalledPlugin()
    {
        var plugin = GoogleWorkspacePlugin();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.AnyAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _stateProtector.Protect(Arg.Is<PluginOAuthStateDto>(state =>
                state.UserId == UserId && state.PluginKey == PluginConstants.GoogleWorkspace))
            .Returns("state-token");
        _oauthClient.BuildAuthorizationUrl(
                plugin,
                Arg.Is<IReadOnlyList<string>>(scopes =>
                    scopes.Contains("https://www.googleapis.com/auth/drive.readonly")),
                "state-token",
                Arg.Any<PluginOAuthStateDto>())
            .Returns("https://accounts.google.test/oauth");

        var result = await CreateSut().GetConnectUrlAsync(PluginConstants.GoogleWorkspace, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://accounts.google.test/oauth", result.Value!.Url);
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_StoresEncryptedPersonalConnection()
    {
        var plugin = GoogleWorkspacePlugin();
        _stateProtector.Unprotect("state-token")
            .Returns(new PluginOAuthStateDto(UserId, PluginConstants.GoogleWorkspace));
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.AnyAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((PluginConnection?)null);
        _oauthClient.ExchangeCodeAsync(plugin, "oauth-code", Arg.Any<PluginOAuthStateDto>(), Arg.Any<CancellationToken>())
            .Returns(new PluginOAuthTokenDto(
                "google-user-id",
                "user@example.com",
                ["https://www.googleapis.com/auth/drive.readonly"],
                "access-token",
                "refresh-token",
                DateTime.UtcNow.AddHours(1)));

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(PluginConstants.GoogleWorkspace, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, result.Value!.Status);
        Assert.Equal("user@example.com", result.Value.ProviderEmail);
        await _connectionRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginConnection>(connection =>
                    connection.UserId == UserId
                    && connection.PluginId == PluginId
                    && connection.ProviderAccountId == "google-user-id"
                    && connection.ProviderEmail == "user@example.com"
                    && connection.EncryptedAccessToken == "protected:access-token"
                    && connection.EncryptedRefreshToken == "protected:refresh-token"
                    && connection.Status == PluginConstants.ConnectionStatus.Connected),
                Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_ClearsExpiredStatus_WhenUserReconnects()
    {
        var plugin = GoogleWorkspacePlugin();
        var expiredConnection = new PluginConnection
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            PluginId = PluginId,
            Status = PluginConstants.ConnectionStatus.Expired,
            EncryptedAccessToken = "protected:stale-access-token",
            EncryptedRefreshToken = "protected:revoked-refresh-token",
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(-3),
        };
        ConfigureInstalledPlugin(plugin);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(expiredConnection);
        _oauthClient.ExchangeCodeAsync(plugin, "oauth-code", Arg.Any<PluginOAuthStateDto>(), Arg.Any<CancellationToken>())
            .Returns(new PluginOAuthTokenDto(
                "google-user-id",
                "user@example.com",
                ["https://www.googleapis.com/auth/drive.readonly"],
                "new-access-token",
                "new-refresh-token",
                DateTime.UtcNow.AddHours(1)));

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(PluginConstants.GoogleWorkspace, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, result.Value!.Status);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, expiredConnection.Status);
        Assert.Equal("protected:new-access-token", expiredConnection.EncryptedAccessToken);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_MarksExpired_WhenFirstConsentOmitsRefreshToken()
    {
        var plugin = GoogleWorkspacePlugin();
        ConfigureInstalledPlugin(plugin);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((PluginConnection?)null);
        _oauthClient.ExchangeCodeAsync(plugin, "oauth-code", Arg.Any<PluginOAuthStateDto>(), Arg.Any<CancellationToken>())
            .Returns(new PluginOAuthTokenDto(
                "google-user-id",
                "user@example.com",
                ["https://www.googleapis.com/auth/drive.readonly"],
                "access-token",
                null,
                DateTime.UtcNow.AddHours(1)));

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(PluginConstants.GoogleWorkspace, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Expired, result.Value!.Status);
        await _connectionRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginConnection>(connection =>
                    connection.Status == PluginConstants.ConnectionStatus.Expired
                    && connection.ProviderEmail == "user@example.com"
                    && connection.EncryptedAccessToken == null
                    && connection.EncryptedRefreshToken == null
                    && connection.AccessTokenExpiresAt == null),
                Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_KeepsExpired_WhenReconnectOmitsNewRefreshToken()
    {
        var plugin = GoogleWorkspacePlugin();
        var expiredConnection = new PluginConnection
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            PluginId = PluginId,
            Status = PluginConstants.ConnectionStatus.Expired,
            EncryptedAccessToken = "protected:stale-access-token",
            EncryptedRefreshToken = "protected:revoked-refresh-token",
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(-3),
        };
        ConfigureInstalledPlugin(plugin);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(expiredConnection);
        _oauthClient.ExchangeCodeAsync(plugin, "oauth-code", Arg.Any<PluginOAuthStateDto>(), Arg.Any<CancellationToken>())
            .Returns(new PluginOAuthTokenDto(
                "google-user-id",
                "user@example.com",
                ["https://www.googleapis.com/auth/drive.readonly"],
                "new-access-token",
                null,
                DateTime.UtcNow.AddHours(1)));

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(PluginConstants.GoogleWorkspace, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Expired, result.Value!.Status);
        Assert.Equal(PluginConstants.ConnectionStatus.Expired, expiredConnection.Status);
        Assert.Null(expiredConnection.EncryptedAccessToken);
        Assert.Null(expiredConnection.EncryptedRefreshToken);
        Assert.Null(expiredConnection.AccessTokenExpiresAt);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_ReusesStoredRefreshToken_WhenConnectedUserReconsents()
    {
        var plugin = GoogleWorkspacePlugin();
        var connected = ConnectedConnection();
        ConfigureInstalledPlugin(plugin);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(connected);
        _oauthClient.ExchangeCodeAsync(plugin, "oauth-code", Arg.Any<PluginOAuthStateDto>(), Arg.Any<CancellationToken>())
            .Returns(new PluginOAuthTokenDto(
                "google-user-id",
                "user@example.com",
                ["https://www.googleapis.com/auth/drive.readonly"],
                "new-access-token",
                null,
                DateTime.UtcNow.AddHours(1)));

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(PluginConstants.GoogleWorkspace, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, result.Value!.Status);
        Assert.Equal("protected:new-access-token", connected.EncryptedAccessToken);
        Assert.Equal("protected:refresh-token", connected.EncryptedRefreshToken);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_PersistsNewAccessToken_AndKeepsStoredRefreshToken_WhenProviderOmitsIt()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        var newExpiry = DateTime.UtcNow.AddHours(1);
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultMapper.Succeeded(
                new PluginOAuthTokenDto(null, null, [], "fresh-access-token", null, newExpiry)));

        var result = await CreateSut().RefreshAccessTokenAsync(plugin, connection);

        Assert.True(result.IsSuccess);
        Assert.Equal("protected:fresh-access-token", connection.EncryptedAccessToken);
        // Google only hands out a refresh token on first consent - dropping it here would break
        // every later refresh.
        Assert.Equal("protected:refresh-token", connection.EncryptedRefreshToken);
        Assert.Equal(newExpiry, connection.AccessTokenExpiresAt);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
        Assert.NotNull(connection.TokenRotatedAt);
        _connectionRepository.Received(1).Update(connection);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_RotatesStoredRefreshToken_WhenProviderReturnsNewOne()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultMapper.Succeeded(new PluginOAuthTokenDto(
                null,
                null,
                [],
                "fresh-access-token",
                "rotated-refresh-token",
                DateTime.UtcNow.AddHours(1))));

        var result = await CreateSut().RefreshAccessTokenAsync(plugin, connection);

        Assert.True(result.IsSuccess);
        Assert.Equal("protected:rotated-refresh-token", connection.EncryptedRefreshToken);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_MarksConnectionExpired_WhenProviderRejectsTheGrant()
    {
        // The rejection that ends a connection is specifically an invalid_grant-shaped one: the
        // provider looked at the stored refresh token and refused it.
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultMapper.GrantRejected(
                "Google token endpoint returned 400 (invalid_grant)."));

        var result = await CreateSut().RefreshAccessTokenAsync(plugin, connection);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Expired, connection.Status);
        _connectionRepository.Received(1).Update(connection);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_LeavesConnectionConnected_WhenProviderIsUnavailable()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultMapper.ProviderUnavailable(
                "Google token endpoint returned 503."));

        var result = await CreateSut().RefreshAccessTokenAsync(plugin, connection);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.ErrorCode);
        // The whole point: a bad minute at Google must not cost the user a browser re-consent.
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
        Assert.Equal("protected:stale-access-token", connection.EncryptedAccessToken);
        Assert.Equal("protected:refresh-token", connection.EncryptedRefreshToken);
        _connectionRepository.DidNotReceive().Update(Arg.Any<PluginConnection>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_LeavesConnectionConnected_WhenProviderRateLimits()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(PluginOAuthRefreshResultMapper.ProviderRateLimited(
                "Google token endpoint returned 429 (rateLimitExceeded)."));

        var result = await CreateSut().RefreshAccessTokenAsync(plugin, connection);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderRateLimited, result.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
        _connectionRepository.DidNotReceive().Update(Arg.Any<PluginConnection>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_LeavesConnectionConnected_WhenTheClientThrowsUnclassified()
    {
        // A fault the OAuth client did not foresee is not evidence the grant is dead. Degrading to
        // transient keeps an unexpected bug from silently expiring every connection it touches.
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        _oauthClient.RefreshAccessTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns<PluginOAuthRefreshResultDto>(_ => throw new HttpRequestException("Connection reset."));

        var result = await CreateSut().RefreshAccessTokenAsync(plugin, connection);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ProviderUnavailable, result.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
        _connectionRepository.DidNotReceive().Update(Arg.Any<PluginConnection>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_MarksConnectionExpired_WhenStoredMaterialWillNotDecrypt()
    {
        // A rotated Data Protection key ring makes the stored refresh token unusable forever.
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        _credentialProtector.Unprotect("protected:refresh-token")
            .Returns<string>(_ => throw new InvalidOperationException("The key was not found in the key ring."));

        var result = await CreateSut().RefreshAccessTokenAsync(plugin, connection);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Expired, connection.Status);
        await _oauthClient.DidNotReceive()
            .RefreshAccessTokenAsync(Arg.Any<Plugin>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_MarksConnectionExpired_WhenNoRefreshTokenStored()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        connection.EncryptedRefreshToken = null;

        var result = await CreateSut().RefreshAccessTokenAsync(plugin, connection);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.ConnectionRequired, result.ErrorCode);
        Assert.Equal(PluginConstants.ConnectionStatus.Expired, connection.Status);
        await _oauthClient.DidNotReceive()
            .RefreshAccessTokenAsync(Arg.Any<Plugin>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_RevokesRefreshToken_AndClearsStoredCredentials()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(connection);

        var result = await CreateSut().DisconnectAsync(PluginConstants.GoogleWorkspace, UserId);

        Assert.True(result.IsSuccess);
        await _oauthClient.Received(1).RevokeTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>());
        Assert.Equal(PluginConstants.ConnectionStatus.Revoked, connection.Status);
        Assert.Null(connection.EncryptedAccessToken);
        Assert.Null(connection.EncryptedRefreshToken);
        Assert.Null(connection.AccessTokenExpiresAt);
        Assert.Null(connection.TokenRotatedAt);
        _connectionRepository.Received(1).Update(connection);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_RevokesAccessToken_WhenNoRefreshTokenExists()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        connection.EncryptedRefreshToken = null;
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(connection);

        var result = await CreateSut().DisconnectAsync(PluginConstants.GoogleWorkspace, UserId);

        Assert.True(result.IsSuccess);
        await _oauthClient.Received(1).RevokeTokenAsync(plugin, "stale-access-token", Arg.Any<CancellationToken>());
        Assert.Equal(PluginConstants.ConnectionStatus.Revoked, connection.Status);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_StillRevokesLocalConnection_WhenProviderRevokeFails()
    {
        var plugin = GoogleWorkspacePlugin();
        var connection = ConnectedConnection();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(connection);
        _oauthClient.RevokeTokenAsync(plugin, "refresh-token", Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("provider unavailable"));

        var result = await CreateSut().DisconnectAsync(PluginConstants.GoogleWorkspace, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Revoked, connection.Status);
        Assert.Null(connection.EncryptedAccessToken);
        Assert.Null(connection.EncryptedRefreshToken);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static PluginConnection ConnectedConnection()
    {
        return new PluginConnection
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            PluginId = PluginId,
            Status = PluginConstants.ConnectionStatus.Connected,
            EncryptedAccessToken = "protected:stale-access-token",
            EncryptedRefreshToken = "protected:refresh-token",
            AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-5),
            ScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
        };
    }

    private void ConfigureInstalledPlugin(Plugin plugin)
    {
        _stateProtector.Unprotect("state-token")
            .Returns(new PluginOAuthStateDto(UserId, PluginConstants.GoogleWorkspace));
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.AnyAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private PluginConnectionService CreateSut()
    {
        return new PluginConnectionService(
            _unitOfWork,
            new TestPluginProviderResolver(oauthClient: _oauthClient),
            _stateProtector,
            _credentialProtector,
            NullLogger<PluginConnectionService>.Instance,
            new TestMcpClientProvisioner());
    }

    private static Plugin GoogleWorkspacePlugin()
    {
        return new Plugin
        {
            Id = PluginId,
            PluginKey = PluginConstants.GoogleWorkspace,
            Label = "Google Workspace",
            Description = "Work across Google Drive and Calendar.",
            Provider = "google",
            IsActive = true,
            RequiredScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
            ToolsJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
