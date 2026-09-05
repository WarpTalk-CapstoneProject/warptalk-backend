using System.Linq.Expressions;
using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

public class PluginInstallationServiceTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PluginId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginRepository _pluginRepository = Substitute.For<IPluginRepository>();
    private readonly IPluginInstallationRepository _installationRepository = Substitute.For<IPluginInstallationRepository>();
    private readonly IPluginConnectionRepository _connectionRepository = Substitute.For<IPluginConnectionRepository>();

    public PluginInstallationServiceTests()
    {
        _unitOfWork.PluginRepository.Returns(_pluginRepository);
        _unitOfWork.PluginInstallationRepository.Returns(_installationRepository);
        _unitOfWork.PluginConnectionRepository.Returns(_connectionRepository);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);
    }

    [Fact]
    public async Task ListCatalogAsync_UsesOnlyCurrentUsersPersonalInstallAndConnection()
    {
        var plugin = GoogleWorkspacePlugin();
        _pluginRepository.FindAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([plugin]);
        _installationRepository.FindAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([
                new PluginInstallation
                {
                    Id = Guid.NewGuid(),
                    UserId = UserId,
                    PluginId = PluginId,
                    Status = PluginConstants.InstallationStatus.Installed,
                    InstalledAt = DateTime.UtcNow,
                }
            ]);
        _connectionRepository.FindAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([
                new PluginConnection
                {
                    Id = Guid.NewGuid(),
                    UserId = UserId,
                    PluginId = PluginId,
                    Status = PluginConstants.ConnectionStatus.Connected,
                    ProviderEmail = "user@example.com",
                    // Only Drive was granted at Google's consent screen - the catalog item must
                    // reflect that partial grant rather than implying every scope was given.
                    ScopesJson = "[\"https://www.googleapis.com/auth/drive.readonly\"]",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            ]);

        var result = await CreateSut().ListCatalogAsync(UserId);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!);
        Assert.Equal(PluginConstants.InstallationStatus.Installed, item.InstallationStatus);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, item.ConnectionStatus);
        Assert.Equal("user@example.com", item.ConnectedAccountEmail);
        Assert.Equal(["https://www.googleapis.com/auth/drive.readonly"], item.GrantedScopes);
        await _installationRepository.Received(1)
            .FindAsync(
                Arg.Is<Expression<Func<PluginInstallation, bool>>>(predicate =>
                    predicate.Compile().Invoke(new PluginInstallation { UserId = UserId, PluginId = PluginId })
                    && !predicate.Compile().Invoke(new PluginInstallation { UserId = OtherUserId, PluginId = PluginId })),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_AddsPersonalInstallation_WhenPluginIsKnownAndNotInstalled()
    {
        var plugin = GoogleWorkspacePlugin();
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((PluginInstallation?)null);

        var result = await CreateSut().InstallAsync(PluginConstants.GoogleWorkspace, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.InstallationStatus.Installed, result.Value!.InstallationStatus);
        await _installationRepository.Received(1)
            .AddAsync(
                Arg.Is<PluginInstallation>(installation =>
                    installation.UserId == UserId
                    && installation.PluginId == PluginId
                    && installation.Status == PluginConstants.InstallationStatus.Installed),
                Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisableAsync_DisablesOnlyTheCurrentUsersInstallation()
    {
        var plugin = GoogleWorkspacePlugin();
        var installation = new PluginInstallation
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            PluginId = PluginId,
            Status = PluginConstants.InstallationStatus.Installed,
            InstalledAt = DateTime.UtcNow,
        };
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.FirstOrDefaultAsync(
                Arg.Is<Expression<Func<PluginInstallation, bool>>>(predicate =>
                    predicate.Compile().Invoke(installation)
                    && !predicate.Compile().Invoke(new PluginInstallation { UserId = OtherUserId, PluginId = PluginId })),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(installation);

        var result = await CreateSut().DisableAsync(PluginConstants.GoogleWorkspace, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.InstallationStatus.Disabled, installation.Status);
        Assert.NotNull(installation.DisabledAt);
        _installationRepository.Received(1).Update(installation);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private PluginInstallationService CreateSut()
    {
        return new PluginInstallationService(_unitOfWork, Substitute.For<IPluginCredentialProtector>());
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
            AvatarUrl = "https://example.test/google.svg",
            IsActive = true,
            RequiredScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
            ToolsJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
