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
    private bool _workspaceAllowsPlugins = true;

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
                    ConnectedAt = DateTime.UtcNow,
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
    public async Task InstallAsync_IsNotConnected_ButCarriesTheProvidersExistingGrant()
    {
        // Installing a plugin while the shared Google grant is already live. Installing is not
        // connecting, so the row says not_connected - reporting connected here is how a sibling
        // used to switch itself on. The grant's account and scopes still travel, so a client can
        // see that Connect will reuse them instead of sending the user back through consent.
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
        _connectionRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new PluginConnection
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                PluginId = PluginId,
                Provider = "google",
                Status = PluginConstants.ConnectionStatus.Connected,
                ProviderEmail = "user@example.com",
                ScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
            });

        var result = await CreateSut().InstallAsync(GoogleDriveKey, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.ConnectionStatus.NotConnected, result.Value!.ConnectionStatus);
        Assert.Equal("user@example.com", result.Value.ConnectedAccountEmail);
        // The granted scopes travel too, so a client can apply the same subset test the catalog
        // listing does instead of trusting the status alone.
        Assert.Contains("https://www.googleapis.com/auth/drive.readonly", result.Value.GrantedScopes);
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
        // Removed is not connected: a reinstall must not come back already connected.
        Assert.Null(installation.ConnectedAt);
        _installationRepository.Received(1).Update(installation);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---- WT-646: three Google rows, one grant ------------------------------------------------

    [Fact]
    public async Task ListCatalogAsync_ReportsOnlyTheConnectedGoogleRowConnected_OffTheOneGrant()
    {
        // One grant, two installed rows, one of them connected. Both rows read the grant - that is
        // what lets the unconnected one reuse the account - but only the row the user connected
        // says connected. Reporting both is how connecting Calendar used to switch Meet on.
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
                Installation(PluginId, connected: true),
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
        Assert.Equal(
            PluginConstants.ConnectionStatus.Connected,
            Assert.Single(result.Value, item => item.Key == GoogleDriveKey).ConnectionStatus);
        Assert.Equal(
            PluginConstants.ConnectionStatus.NotConnected,
            Assert.Single(result.Value, item => item.Key == GoogleCalendarKey).ConnectionStatus);
        Assert.All(result.Value, item =>
        {
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

    // ---- WT-646: workspace plugin policy ------------------------------------------------------

    private static readonly Guid WorkspaceId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    [Fact]
    public async Task ListCatalogAsync_AppliesNoPolicy_WhenNoWorkspaceIsNamed()
    {
        // The personal plugins page's own call. It names no workspace, so no workspace policy
        // applies and nothing is blocked - which is exactly how this behaved before WT-646.
        ConfigureCatalog(GoogleDrivePlugin(), GoogleCalendarPlugin());
        _workspaceAllowsPlugins = false;

        var result = await CreateSut().ListCatalogAsync(UserId);

        Assert.True(result.IsSuccess);
        Assert.All(result.Value!, item => Assert.Null(item.WorkspacePolicyBlockReason));
    }

    [Fact]
    public async Task ListCatalogAsync_ReportsBlockedRowsRatherThanHidingThem()
    {
        // What happens to a user who installed and connected google_calendar and whose admin has
        // since turned plugins off for the workspace: the rows stay, so they can still see and
        // revoke a live Google grant, and each says why it is blocked.
        ConfigureCatalog(GoogleDrivePlugin(), GoogleCalendarPlugin());
        _installationRepository.FindAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([Installation(PluginId), Installation(CalendarPluginId)]);
        _workspaceAllowsPlugins = false;

        var result = await CreateSut().ListCatalogAsync(UserId, WorkspaceId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value, item =>
            Assert.Equal(PluginConstants.WorkspacePolicyMessages.PluginsDisabled, item.WorkspacePolicyBlockReason));

        // Still reported as installed. The rows are untouched; the block is a verdict, not a
        // rewrite of what the user has.
        var calendar = Assert.Single(result.Value, item => item.Key == GoogleCalendarKey);
        Assert.Equal(PluginConstants.InstallationStatus.Installed, calendar.InstallationStatus);
    }

    [Fact]
    public async Task ListCatalogAsync_BlocksNothing_WhenTheWorkspaceAllowsPlugins()
    {
        ConfigureCatalog(GoogleDrivePlugin(), GoogleCalendarPlugin());
        _workspaceAllowsPlugins = true;

        var result = await CreateSut().ListCatalogAsync(UserId, WorkspaceId);

        Assert.True(result.IsSuccess);
        Assert.All(result.Value!, item => Assert.Null(item.WorkspacePolicyBlockReason));
    }

    [Fact]
    public async Task InstallAsync_RefusesAPluginTheWorkspaceDoesNotAllow_AndWritesNothing()
    {
        ConfigureInstallablePlugin(GoogleCalendarPlugin());
        _workspaceAllowsPlugins = false;

        var result = await CreateSut().InstallAsync(GoogleCalendarKey, UserId, WorkspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        await _installationRepository.DidNotReceive()
            .AddAsync(Arg.Any<PluginInstallation>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ReportsAnUnknownKeyAsUnknown_NotAsForbidden()
    {
        // Order matters: judging the policy before the catalog lookup would turn every typo into
        // "your workspace does not allow this", and send the user to an admin over a misspelling.
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((Plugin?)null);
        _workspaceAllowsPlugins = false;

        var result = await CreateSut().InstallAsync("no_such_plugin", UserId, WorkspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.UnknownPlugin, result.ErrorCode);
    }

    [Fact]
    public async Task DisableAsync_IsNeverGatedByWorkspacePolicy()
    {
        // Removing a plugin has to stay possible whatever the policy says. A workspace that has
        // just excluded a plugin is precisely when a user most needs to be able to uninstall it.
        var installation = Installation(PluginId);
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(GoogleDrivePlugin());
        _installationRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(installation);
        _workspaceAllowsPlugins = false;

        var result = await CreateSut().DisableAsync(GoogleDriveKey, UserId);

        Assert.True(result.IsSuccess);
        Assert.Equal(PluginConstants.InstallationStatus.Disabled, installation.Status);
    }

    private void ConfigureCatalog(params Plugin[] plugins)
    {
        _pluginRepository.FindAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugins);
        _installationRepository.FindAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        _connectionRepository.FindAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private void ConfigureInstallablePlugin(Plugin plugin)
    {
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
    }

    private static PluginInstallation Installation(Guid pluginId, bool connected = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            PluginId = pluginId,
            Status = PluginConstants.InstallationStatus.Installed,
            InstalledAt = DateTime.UtcNow,
            ConnectedAt = connected ? DateTime.UtcNow : null,
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

    // ---- WT-687: per-tool choices --------------------------------------------------------------

    [Fact]
    public async Task UpdateToolPolicyAsync_MergesKnownToolsIntoTheCallersInstallation_AndKeepsOtherConfig()
    {
        var plugin = DriveWithTools();
        var installation = InstalledDrive("""{"installedFrom":"assistant_plugins","toolPolicy":{"drive_get_file":"blocked"}}""");
        ConfigurePolicyTarget(plugin, installation);

        var result = await CreateSut().UpdateToolPolicyAsync(
            GoogleDriveKey,
            UserId,
            new Dictionary<string, string> { ["drive_search"] = "approval", ["no_such_tool"] = "blocked" });

        Assert.True(result.IsSuccess);
        Assert.Contains("\"installedFrom\":\"assistant_plugins\"", installation.ConfigJson);
        var stored = WarpTalk.AssistantService.Application.Helpers.PluginToolPolicyStore.Read(installation.ConfigJson);
        Assert.Equal("approval", stored["drive_search"]);
        // Untouched by the update, and the unknown name was dropped rather than stored.
        Assert.Equal("blocked", stored["drive_get_file"]);
        Assert.False(stored.ContainsKey("no_such_tool"));
        Assert.Equal("approval", result.Value!.Tools.Single(tool => tool.Name == "drive_search").Policy);
        _installationRepository.Received(1).Update(installation);
    }

    [Fact]
    public async Task UpdateToolPolicyAsync_RefusesAValueThatIsNotAToolSetting()
    {
        var installation = InstalledDrive(null);
        ConfigurePolicyTarget(DriveWithTools(), installation);

        var result = await CreateSut().UpdateToolPolicyAsync(
            GoogleDriveKey,
            UserId,
            new Dictionary<string, string> { ["drive_search"] = "always" });

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.InvalidToolPolicy, result.ErrorCode);
        Assert.Null(installation.ConfigJson);
    }

    [Fact]
    public async Task UpdateToolPolicyAsync_RefusesAPluginTheCallerHasNotInstalled()
    {
        ConfigurePolicyTarget(DriveWithTools(), installation: null);

        var result = await CreateSut().UpdateToolPolicyAsync(
            GoogleDriveKey,
            UserId,
            new Dictionary<string, string> { ["drive_search"] = "blocked" });

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PluginNotInstalled, result.ErrorCode);
    }

    [Fact]
    public async Task ListCatalogAsync_DefaultsEachToolFromItsEffect_WhenTheUserHasChosenNothing()
    {
        _pluginRepository.FindAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([DriveWithTools()]);
        _installationRepository.FindAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PluginInstallation>());
        _connectionRepository.FindAsync(
                Arg.Any<Expression<Func<PluginConnection, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PluginConnection>());

        var result = await CreateSut().ListCatalogAsync(UserId);

        var tools = Assert.Single(result.Value!).Tools;
        Assert.Equal("allow", tools.Single(tool => tool.Name == "drive_search").Policy);
        Assert.Equal("approval", tools.Single(tool => tool.Name == "drive_get_file").Policy);
    }

    private static Plugin DriveWithTools()
    {
        var plugin = GoogleDrivePlugin();
        // One read and one write tool; the write one only so the effect default has two answers.
        plugin.ToolsJson = """
            [
              { "name": "drive_search", "pluginKey": "google_drive", "label": "Search", "description": "", "effect": "read", "requiredScopes": [], "parameters": {} },
              { "name": "drive_get_file", "pluginKey": "google_drive", "label": "Get file", "description": "", "effect": "write", "requiredScopes": [], "parameters": {} }
            ]
            """;
        return plugin;
    }

    private static PluginInstallation InstalledDrive(string? configJson) => new()
    {
        Id = Guid.NewGuid(),
        UserId = UserId,
        PluginId = PluginId,
        Status = PluginConstants.InstallationStatus.Installed,
        InstalledAt = DateTime.UtcNow,
        ConfigJson = configJson,
    };

    private void ConfigurePolicyTarget(Plugin plugin, PluginInstallation? installation)
    {
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(plugin);
        _installationRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<PluginInstallation, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(installation);
    }

    private PluginInstallationService CreateSut()
    {
        return new PluginInstallationService(
            _unitOfWork,
            Substitute.For<IPluginCredentialProtector>(),
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
            AvatarUrl = "https://example.test/google.svg",
            IsActive = true,
            RequiredScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
            ToolsJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
