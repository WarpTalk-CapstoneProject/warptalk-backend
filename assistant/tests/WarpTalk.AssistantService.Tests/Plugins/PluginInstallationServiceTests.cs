using System.Linq.Expressions;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

public class PluginInstallationServiceTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PluginId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Guid CalendarPluginId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RemotePluginId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private const string GoogleDriveKey = "google_drive";
    private const string GoogleCalendarKey = "google_calendar";
    private const string RemoteAppKey = "remote_app";

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginRepository _pluginRepository = Substitute.For<IPluginRepository>();
    private readonly IPluginInstallationRepository _installationRepository = Substitute.For<IPluginInstallationRepository>();
    private readonly IPluginConnectionRepository _connectionRepository = Substitute.For<IPluginConnectionRepository>();

    // WT-646. The workspace policy every test runs under, defaulting to what a workspace service
    // older than the ticket reports: no allowlist, plugins permitted, member installs permitted.
    // That is what every workspace in the product looks like today, so leaving it alone is how the
    // pre-WT-646 tests keep asserting pre-WT-646 behaviour.
    private WorkspacePluginPolicySnapshot _workspacePolicy = TestWorkspacePluginPolicy.LegacyPeer(allowAnyPlugins: true);
    private string _callerRole = WorkspaceRoleConstants.Owner;

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
        var plugin = GoogleDrivePlugin();
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
                    Provider = PluginConstants.Providers.Google,
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
        var plugin = GoogleDrivePlugin();
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

        var result = await CreateSut().InstallAsync(GoogleDriveKey, UserId);

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
        var plugin = GoogleDrivePlugin();
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

        var result = await CreateSut().DisableAsync(GoogleDriveKey, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.InstallationStatus.Disabled, installation.Status);
        Assert.NotNull(installation.DisabledAt);
        _installationRepository.Received(1).Update(installation);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---- WT-646: three Google rows, one grant ------------------------------------------------

    [Fact]
    public async Task ListCatalogAsync_ShowsEveryGoogleRowConnected_OffTheOneGrant()
    {
        // The tile-level regression this ticket exists to prevent: with the connection matched on
        // plugin id, whichever Google tile did not happen to start the consent would render a
        // "Connect" button for an account the user has already connected.
        var drive = GoogleDrivePlugin();
        var calendar = GoogleCalendarPlugin();
        _pluginRepository.FindAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([drive, calendar]);
        _installationRepository.FindAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([
                Installation(PluginId),
                Installation(CalendarPluginId),
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
                    // Obtained through Drive. Calendar has to find it anyway.
                    PluginId = PluginId,
                    Provider = PluginConstants.Providers.Google,
                    Status = PluginConstants.ConnectionStatus.Connected,
                    ProviderEmail = "user@example.com",
                    ScopesJson = "[\"https://www.googleapis.com/auth/drive.readonly\"]",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            ]);

        var result = await CreateSut().ListCatalogAsync(UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value, item =>
        {
            Assert.Equal(PluginConstants.ConnectionStatus.Connected, item.ConnectionStatus);
            Assert.Equal("user@example.com", item.ConnectedAccountEmail);
            // And installing Calendar did not invent a calendar scope: the tile reports exactly
            // what Google granted, so the UI can still tell the user a reconnect is needed.
            Assert.Equal(["https://www.googleapis.com/auth/drive.readonly"], item.GrantedScopes);
        });
        var calendarItem = Assert.Single(result.Value, item => item.Key == GoogleCalendarKey);
        Assert.Equal(["https://www.googleapis.com/auth/calendar.events"], calendarItem.RequiredScopes);
    }

    [Fact]
    public async Task ListCatalogAsync_DoesNotLendAGoogleGrantToAnUnrelatedMcpPlugin()
    {
        var drive = GoogleDrivePlugin();
        var remote = RemoteMcpPlugin();
        _pluginRepository.FindAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([drive, remote]);
        _installationRepository.FindAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([Installation(PluginId), Installation(RemotePluginId)]);
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
                    Provider = PluginConstants.Providers.Google,
                    Status = PluginConstants.ConnectionStatus.Connected,
                    ProviderEmail = "user@example.com",
                    ScopesJson = "[]",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                }
            ]);

        var result = await CreateSut().ListCatalogAsync(UserId);

        Assert.True(result.IsSuccess);
        var remoteItem = Assert.Single(result.Value!, item => item.Key == RemoteAppKey);
        Assert.Equal(PluginConstants.ConnectionStatus.NotConnected, remoteItem.ConnectionStatus);
        Assert.Null(remoteItem.ConnectedAccountEmail);
    }

    [Fact]
    public async Task CreateMcpPluginAsync_RejectsAKeyThatCollidesWithAnExistingProvider()
    {
        // An MCP row takes its key as its provider, and the provider is the identity of a grant.
        // A row keyed 'google' would be handed the existing Google connection, refresh token and
        // all, and its tools would run against Google's grant.
        _pluginRepository.AnyAsync(Arg.Any<Expression<Func<Plugin, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Expression<Func<Plugin, bool>>>().Compile().Invoke(
                new Plugin
                {
                    PluginKey = GoogleDriveKey,
                    Provider = PluginConstants.Providers.Google,
                }));

        var result = await CreateSut().CreateMcpPluginAsync(
            new CreateMcpPluginRequest(
                PluginConstants.Providers.Google,
                "Impostor",
                "Claims to be Google.",
                "https://impostor.test/mcp"),
            UserId);

        Assert.False(result.IsSuccess);
        Assert.Contains("provider", result.Error!, StringComparison.OrdinalIgnoreCase);
        await _pluginRepository.DidNotReceive().AddAsync(Arg.Any<Plugin>(), Arg.Any<CancellationToken>());
    }

    private static PluginInstallation Installation(Guid pluginId) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            PluginId = pluginId,
            Status = PluginConstants.InstallationStatus.Installed,
            InstalledAt = DateTime.UtcNow,
        };

    private static Plugin GoogleCalendarPlugin()
    {
        return new Plugin
        {
            Id = CalendarPluginId,
            PluginKey = GoogleCalendarKey,
            Label = "Google Calendar",
            Description = "List events on your Google Calendar and create new ones.",
            Provider = PluginConstants.Providers.Google,
            AvatarUrl = "https://example.test/google-calendar.svg",
            IsActive = true,
            RequiredScopesJson = """["https://www.googleapis.com/auth/calendar.events"]""",
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

    private PluginInstallationService CreateSut()
    {
        return new PluginInstallationService(
            _unitOfWork,
            Substitute.For<IPluginCredentialProtector>(),
            TestWorkspacePluginPolicy.Guard(_workspacePolicy, _callerRole));
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
            AvatarUrl = "https://example.test/google.svg",
            IsActive = true,
            RequiredScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
            ToolsJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
