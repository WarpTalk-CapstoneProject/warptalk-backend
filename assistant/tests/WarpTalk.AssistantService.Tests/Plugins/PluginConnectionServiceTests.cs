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
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

public class PluginConnectionServiceTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PluginId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Guid CalendarPluginId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid MeetPluginId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid RemotePluginId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    // Three catalog rows, one provider. PluginId above belongs to google_drive, which is the row
    // these fixtures treat as the one the user first consented through.
    private const string GoogleDriveKey = "google_drive";
    private const string GoogleCalendarKey = "google_calendar";
    private const string GoogleMeetKey = "google_meet";
    private const string RemoteAppKey = "remote_app";

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginRepository _pluginRepository = Substitute.For<IPluginRepository>();
    private readonly IPluginInstallationRepository _installationRepository = Substitute.For<IPluginInstallationRepository>();
    private readonly IPluginConnectionRepository _connectionRepository = Substitute.For<IPluginConnectionRepository>();
    private readonly IPluginOAuthClient _oauthClient = Substitute.For<IPluginOAuthClient>();
    private readonly IPluginOAuthStateProtector _stateProtector = Substitute.For<IPluginOAuthStateProtector>();
    private readonly IPluginCredentialProtector _credentialProtector = Substitute.For<IPluginCredentialProtector>();

    // WT-646. Defaults to what a workspace service older than the ticket reports, which is every
    // workspace in the product today: no allowlist, plugins permitted. Tests that predate the
    // policy therefore keep asserting the behaviour they always asserted.
    private bool _workspaceAllowsPlugins = true;

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
        var plugin = GoogleDrivePlugin();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.AnyAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateSut().GetConnectUrlAsync(GoogleDriveKey, UserId);

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
        var plugin = GoogleDrivePlugin();
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
                state.UserId == UserId && state.PluginKey == GoogleDriveKey))
            .Returns("state-token");
        _oauthClient.BuildAuthorizationUrl(
                plugin,
                Arg.Is<IReadOnlyList<string>>(scopes =>
                    scopes.Contains("https://www.googleapis.com/auth/drive.readonly")),
                "state-token",
                Arg.Any<PluginOAuthStateDto>())
            .Returns("https://accounts.google.test/oauth");

        var result = await CreateSut().GetConnectUrlAsync(GoogleDriveKey, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://accounts.google.test/oauth", result.Value!.Url);
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_StoresEncryptedPersonalConnection()
    {
        var plugin = GoogleDrivePlugin();
        _stateProtector.Unprotect("state-token")
            .Returns(new PluginOAuthStateDto(UserId, GoogleDriveKey));
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
            .CompleteOAuthCallbackAsync(GoogleDriveKey, "oauth-code", "state-token");

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
        var plugin = GoogleDrivePlugin();
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
            .CompleteOAuthCallbackAsync(GoogleDriveKey, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, result.Value!.Status);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, expiredConnection.Status);
        Assert.Equal("protected:new-access-token", expiredConnection.EncryptedAccessToken);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_MarksExpired_WhenFirstConsentOmitsRefreshToken()
    {
        var plugin = GoogleDrivePlugin();
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
            .CompleteOAuthCallbackAsync(GoogleDriveKey, "oauth-code", "state-token");

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
        var plugin = GoogleDrivePlugin();
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
            .CompleteOAuthCallbackAsync(GoogleDriveKey, "oauth-code", "state-token");

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
        var plugin = GoogleDrivePlugin();
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
            .CompleteOAuthCallbackAsync(GoogleDriveKey, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, result.Value!.Status);
        Assert.Equal("protected:new-access-token", connected.EncryptedAccessToken);
        Assert.Equal("protected:refresh-token", connected.EncryptedRefreshToken);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_PersistsNewAccessToken_AndKeepsStoredRefreshToken_WhenProviderOmitsIt()
    {
        var plugin = GoogleDrivePlugin();
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
        var plugin = GoogleDrivePlugin();
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
        var plugin = GoogleDrivePlugin();
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
        var plugin = GoogleDrivePlugin();
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
        var plugin = GoogleDrivePlugin();
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
        var plugin = GoogleDrivePlugin();
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
        var plugin = GoogleDrivePlugin();
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
        var plugin = GoogleDrivePlugin();
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
        var plugin = GoogleDrivePlugin();
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

        var result = await CreateSut().DisconnectAsync(GoogleDriveKey, UserId);

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
        var plugin = GoogleDrivePlugin();
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

        var result = await CreateSut().DisconnectAsync(GoogleDriveKey, UserId);

        Assert.True(result.IsSuccess);
        await _oauthClient.Received(1).RevokeTokenAsync(plugin, "stale-access-token", Arg.Any<CancellationToken>());
        Assert.Equal(PluginConstants.ConnectionStatus.Revoked, connection.Status);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_StillRevokesLocalConnection_WhenProviderRevokeFails()
    {
        var plugin = GoogleDrivePlugin();
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

        var result = await CreateSut().DisconnectAsync(GoogleDriveKey, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Revoked, connection.Status);
        Assert.Null(connection.EncryptedAccessToken);
        Assert.Null(connection.EncryptedRefreshToken);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---- WT-646: one grant per provider, one redirect URI per provider ------------------------

    [Fact]
    public async Task CompleteOAuthCallbackAsync_LooksTheConnectionUpByProvider_NotByPluginId()
    {
        // The regression this pins: with the lookup keyed on plugin id, connecting Calendar after
        // Drive would find nothing, insert a second row, and hit the (user_id, provider) unique
        // constraint - or, before that constraint existed, quietly split one Google grant in two.
        var calendar = GoogleCalendarPlugin();
        ConfigureInstalledPlugin(calendar, GoogleCalendarKey);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((PluginConnection?)null);
        ConfigureExchange(calendar, ["https://www.googleapis.com/auth/calendar.events"]);

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(GoogleCalendarKey, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        await _connectionRepository.Received(1).FirstOrDefaultAsync(
            Arg.Is<Expression<Func<PluginConnection, bool>>>(predicate =>
                // The grant obtained through the Drive row is the one Calendar has to find.
                predicate.Compile().Invoke(new PluginConnection
                {
                    UserId = UserId,
                    PluginId = PluginId,
                    Provider = PluginConstants.Providers.Google,
                })
                // A different provider's grant is not it.
                && !predicate.Compile().Invoke(new PluginConnection
                {
                    UserId = UserId,
                    PluginId = CalendarPluginId,
                    Provider = "remote_app",
                })),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_WidensTheExistingGoogleGrant_WhenASecondGooglePluginConnects()
    {
        // Drive consented first; the row records that in PluginId. Calendar now consents against
        // the same Google grant, so this must update that row rather than add a second one - and
        // must leave PluginId alone, because it claims only "first obtained through".
        var calendar = GoogleCalendarPlugin();
        var existing = ConnectedConnection();
        ConfigureInstalledPlugin(calendar, GoogleCalendarKey);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(existing);
        ConfigureExchange(calendar, [
            "https://www.googleapis.com/auth/drive.readonly",
            "https://www.googleapis.com/auth/calendar.events",
        ]);

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(GoogleCalendarKey, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        await _connectionRepository.DidNotReceive()
            .AddAsync(Arg.Any<PluginConnection>(), Arg.Any<CancellationToken>());
        _connectionRepository.Received(1).Update(existing);
        Assert.Equal(PluginConstants.Providers.Google, existing.Provider);
        Assert.Equal(PluginId, existing.PluginId);
        Assert.Contains("calendar.events", existing.ScopesJson, StringComparison.Ordinal);
        Assert.Contains("drive.readonly", existing.ScopesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_StampsTheProvider_WhenItCreatesAConnection()
    {
        // provider is NOT NULL with no database default, so a write path that forgets it fails at
        // runtime, not at compile time. This is the test that notices.
        var plugin = GoogleDrivePlugin();
        ConfigureInstalledPlugin(plugin);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((PluginConnection?)null);
        ConfigureExchange(plugin, ["https://www.googleapis.com/auth/drive.readonly"]);

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(GoogleDriveKey, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        await _connectionRepository.Received(1).AddAsync(
            Arg.Is<PluginConnection>(connection =>
                connection.Provider == PluginConstants.Providers.Google
                && connection.PluginId == PluginId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatusAsync_ReportsTheSharedGoogleGrant_ForAGooglePluginThatDidNotStartIt()
    {
        // Meet was installed after the user consented through Drive. If this reported
        // not_connected the tile would offer a connect button for a grant the user already has.
        var meet = GoogleMeetPlugin();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(meet);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ConnectedConnection());

        var result = await CreateSut().GetStatusAsync(GoogleMeetKey, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, result.Value!.Status);
        await _connectionRepository.Received(1).FirstOrDefaultAsync(
            Arg.Is<Expression<Func<PluginConnection, bool>>>(predicate =>
                predicate.Compile().Invoke(new PluginConnection
                {
                    UserId = UserId,
                    PluginId = PluginId,
                    Provider = PluginConstants.Providers.Google,
                })),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteProviderOAuthCallbackAsync_TakesThePluginKeyFromState()
    {
        // The provider-scoped redirect URI has no plugin key in the path. The key rides in the
        // sealed state - the same arrangement the MCP callback uses - so this asserts the plugin
        // actually looked up is the one the state named, not the one the URL happened to mention.
        var calendar = GoogleCalendarPlugin();
        ConfigureInstalledPlugin(calendar, GoogleCalendarKey);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((PluginConnection?)null);
        ConfigureExchange(calendar, ["https://www.googleapis.com/auth/calendar.events"]);

        var result = await CreateSut().CompleteProviderOAuthCallbackAsync(
            PluginConstants.Providers.Google, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(GoogleCalendarKey, result.Value!.PluginKey);
        await _pluginRepository.Received(1).FirstOrDefaultAsync(
            Arg.Is<Expression<Func<Plugin, bool>>>(predicate =>
                predicate.Compile().Invoke(new Plugin { PluginKey = GoogleCalendarKey, IsActive = true })
                && !predicate.Compile().Invoke(new Plugin { PluginKey = GoogleDriveKey, IsActive = true })),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteProviderOAuthCallbackAsync_RejectsAStateForAnotherProvider()
    {
        // A state minted for an MCP plugin must not be redeemable on Google's callback: the code
        // that came back was issued by a different authorization server.
        var remote = RemoteMcpPlugin();
        ConfigureInstalledPlugin(remote, RemoteAppKey);

        var result = await CreateSut().CompleteProviderOAuthCallbackAsync(
            PluginConstants.Providers.Google, "oauth-code", "state-token");

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        // Same opaque message the forged-state path returns, so a prober cannot tell the two apart.
        Assert.Equal("Invalid OAuth state.", result.Error);
        await _oauthClient.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<Plugin>(), Arg.Any<string>(), Arg.Any<PluginOAuthStateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteProviderOAuthCallbackAsync_RejectsAnUnreadableState()
    {
        _stateProtector.Unprotect("tampered-state").Returns(_ => throw new InvalidOperationException("bad payload"));

        var result = await CreateSut().CompleteProviderOAuthCallbackAsync(
            PluginConstants.Providers.Google, "oauth-code", "tampered-state");

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        await _oauthClient.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<Plugin>(), Arg.Any<string>(), Arg.Any<PluginOAuthStateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_StillCompletesOnTheLegacyPerPluginPath()
    {
        // Kept working on purpose: consents already in flight when the provider-scoped redirect URI
        // ships come back here, and so does any environment whose Google Cloud Console entry has
        // not been updated yet.
        var plugin = GoogleDrivePlugin();
        ConfigureInstalledPlugin(plugin);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((PluginConnection?)null);
        ConfigureExchange(plugin, ["https://www.googleapis.com/auth/drive.readonly"]);

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(GoogleDriveKey, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, result.Value!.Status);
        await _connectionRepository.Received(1).AddAsync(
            Arg.Is<PluginConnection>(connection => connection.Provider == PluginConstants.Providers.Google),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_StillRejectsAPathKeyThatDisagreesWithState()
    {
        // The legacy path carries the key twice, and the cross-check that stops them disagreeing
        // has to survive the addition of the provider-scoped route beside it.
        var plugin = GoogleDrivePlugin();
        ConfigureInstalledPlugin(plugin);

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(GoogleCalendarKey, "oauth-code", "state-token");

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        await _oauthClient.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<Plugin>(), Arg.Any<string>(), Arg.Any<PluginOAuthStateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_EndsTheProviderGrant_NotJustTheOnePluginsView()
    {
        // Disconnecting Meet ends the Google grant that Drive and Calendar also ride on. Google
        // revokes per grant, so the alternative would leave rows we believe are healthy pointing
        // at a dead grant.
        var meet = GoogleMeetPlugin();
        var connection = ConnectedConnection();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(meet);
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(connection);

        var result = await CreateSut().DisconnectAsync(GoogleMeetKey, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Revoked, connection.Status);
        await _connectionRepository.Received(1).FirstOrDefaultAsync(
            Arg.Is<Expression<Func<PluginConnection, bool>>>(predicate =>
                predicate.Compile().Invoke(new PluginConnection
                {
                    UserId = UserId,
                    PluginId = PluginId,
                    Provider = PluginConstants.Providers.Google,
                })),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    private void ConfigureExchange(Plugin plugin, string[] grantedScopes)
    {
        _oauthClient.ExchangeCodeAsync(plugin, "oauth-code", Arg.Any<PluginOAuthStateDto>(), Arg.Any<CancellationToken>())
            .Returns(new PluginOAuthTokenDto(
                "google-user-id",
                "user@example.com",
                grantedScopes,
                "access-token",
                "refresh-token",
                DateTime.UtcNow.AddHours(1)));
    }

    private static Plugin GoogleCalendarPlugin() =>
        GooglePlugin(CalendarPluginId, GoogleCalendarKey, "Google Calendar", "https://www.googleapis.com/auth/calendar.events");

    private static Plugin GoogleMeetPlugin() =>
        GooglePlugin(MeetPluginId, GoogleMeetKey, "Google Meet", "https://www.googleapis.com/auth/calendar.events");

    private static Plugin GooglePlugin(Guid id, string key, string label, string scope)
    {
        return new Plugin
        {
            Id = id,
            PluginKey = key,
            Label = label,
            Description = label,
            Provider = PluginConstants.Providers.Google,
            IsActive = true,
            RequiredScopesJson = $"[\"{scope}\"]",
            ToolsJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static Plugin RemoteMcpPlugin()
    {
        return new Plugin
        {
            Id = RemotePluginId,
            PluginKey = RemoteAppKey,
            Label = "Remote App",
            Description = "A remote MCP server.",
            // Its own provider, so its grant can never be confused with Google's.
            Provider = RemoteAppKey,
            Kind = PluginConstants.PluginKind.Mcp,
            McpServerUrl = "https://remote.test/mcp",
            IsActive = true,
            RequiredScopesJson = "[]",
            ToolsJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static PluginConnection ConnectedConnection()
    {
        return new PluginConnection
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            // Obtained through google_drive; serves every Google row from here on.
            PluginId = PluginId,
            Provider = PluginConstants.Providers.Google,
            Status = PluginConstants.ConnectionStatus.Connected,
            EncryptedAccessToken = "protected:stale-access-token",
            EncryptedRefreshToken = "protected:refresh-token",
            AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-5),
            ScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
        };
    }

    private void ConfigureInstalledPlugin(Plugin plugin) =>
        ConfigureInstalledPlugin(plugin, GoogleDriveKey);

    /// <param name="statePluginKey">
    /// The key sealed into the OAuth state. Separate from the plugin argument on purpose: the
    /// provider-scoped callback has no key in its path, so the state is the only thing that says
    /// which plugin the flow was for, and a test has to be able to disagree with it.
    /// </param>
    private void ConfigureInstalledPlugin(Plugin plugin, string statePluginKey)
    {
        _stateProtector.Unprotect("state-token")
            .Returns(new PluginOAuthStateDto(UserId, statePluginKey));
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

    // ---- WT-646: workspace plugin policy ------------------------------------------------------

    private static readonly Guid WorkspaceId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    [Fact]
    public async Task GetConnectUrlAsync_RefusesAPluginTheWorkspaceDoesNotAllow()
    {
        // The case the install gate cannot catch: installed while the workspace still permitted
        // plugins, in a workspace that has since turned them off.
        var plugin = GoogleCalendarPlugin();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.AnyAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _workspaceAllowsPlugins = false;

        var result = await CreateSut().GetConnectUrlAsync(GoogleCalendarKey, UserId, WorkspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        // Refused before the URL exists, so the user is never sent to a consent screen for a
        // grant this workspace would then refuse to use.
        _oauthClient.DidNotReceive()
            .BuildAuthorizationUrl(
                Arg.Any<Plugin>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(),
                Arg.Any<PluginOAuthStateDto>());
    }

    [Fact]
    public async Task GetConnectUrlAsync_IsUnaffectedByWorkspacePolicy_WhenNoWorkspaceIsNamed()
    {
        var plugin = GoogleDrivePlugin();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.AnyAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _stateProtector.Protect(Arg.Any<PluginOAuthStateDto>()).Returns("state-token");
        _oauthClient.BuildAuthorizationUrl(
                plugin,
                Arg.Any<IReadOnlyList<string>>(),
                "state-token",
                Arg.Any<PluginOAuthStateDto>())
            .Returns("https://accounts.google.test/oauth");
        // A policy that would refuse everything, and no workspace to apply it to.
        _workspaceAllowsPlugins = false;

        var result = await CreateSut().GetConnectUrlAsync(GoogleDriveKey, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://accounts.google.test/oauth", result.Value!.Url);
    }

    [Fact]
    public async Task CompleteOAuthCallbackAsync_IsNotGatedByWorkspacePolicy()
    {
        // The callback carries no workspace and arrives after the user has already consented at
        // the provider. Refusing here would strand a real grant rather than prevent one, so the
        // gate lives at GetConnectUrlAsync instead. Asserted so a later change cannot move it here
        // by accident.
        var plugin = GoogleDrivePlugin();
        _stateProtector.Unprotect("state-token")
            .Returns(new PluginOAuthStateDto(UserId, GoogleDriveKey));
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
        _workspaceAllowsPlugins = false;

        var result = await CreateSut()
            .CompleteOAuthCallbackAsync(GoogleDriveKey, "oauth-code", "state-token");

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, result.Value!.Status);
    }

    [Fact]
    public async Task DisconnectAsync_IsNeverGatedByWorkspacePolicy()
    {
        // The counterpart to the catalog reporting a blocked row rather than hiding it: a user
        // holding a grant in a workspace that has since turned plugins off must still be able to
        // revoke it.
        var plugin = GoogleDrivePlugin();
        var connection = new PluginConnection
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            PluginId = PluginId,
            Provider = PluginConstants.Providers.Google,
            Status = PluginConstants.ConnectionStatus.Connected,
            EncryptedRefreshToken = "protected:refresh-token",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
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
        _workspaceAllowsPlugins = false;

        var result = await CreateSut().DisconnectAsync(GoogleDriveKey, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.Revoked, connection.Status);
        Assert.Null(connection.EncryptedRefreshToken);
    }

    private PluginConnectionService CreateSut()
    {
        return new PluginConnectionService(
            _unitOfWork,
            new TestPluginProviderResolver(oauthClient: _oauthClient),
            _stateProtector,
            _credentialProtector,
            NullLogger<PluginConnectionService>.Instance,
            new TestMcpClientProvisioner(),
            TestWorkspacePluginPolicy.Guard(_workspaceAllowsPlugins));
    }

    private static Plugin GoogleDrivePlugin()
    {
        return new Plugin
        {
            Id = PluginId,
            PluginKey = GoogleDriveKey,
            Label = "Google Drive",
            Description = "Search your Google Drive and read the contents of a file.",
            Provider = PluginConstants.Providers.Google,
            IsActive = true,
            RequiredScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
            ToolsJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
