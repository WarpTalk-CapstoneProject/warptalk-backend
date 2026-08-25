using System.Linq.Expressions;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
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
        _credentialProtector.Protect(Arg.Any<string>()).Returns(call => $"protected:{call.Arg<string>()}");
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
                Arg.Any<string>());
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
                "state-token")
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
        _oauthClient.ExchangeCodeAsync(plugin, "oauth-code", Arg.Any<CancellationToken>())
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

    private PluginConnectionService CreateSut()
    {
        return new PluginConnectionService(
            _unitOfWork,
            _oauthClient,
            _stateProtector,
            _credentialProtector);
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
