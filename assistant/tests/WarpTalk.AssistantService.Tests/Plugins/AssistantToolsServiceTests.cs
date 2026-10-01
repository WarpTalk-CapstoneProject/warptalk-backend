using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Messaging;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// <c>GET api/v1/assistant/tools</c>: the worker's manifest, the web search state and the plugin
/// tools, run through the REAL orchestrator and guard so the plugin section is proved to be the
/// list WarpBot is offered, not a copy of its filtering.
/// </summary>
public class AssistantToolsServiceTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DrivePluginId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CalendarPluginId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private const string Manifest = """
        {
          "version": 1,
          "generatedAt": "2026-10-01T10:00:00Z",
          "workerVersion": null,
          "webSearch": { "available": true },
          "tools": [
            { "name": "create_meeting", "category": "meetings", "effect": "write", "audience": "member", "description": "Create a meeting." },
            { "name": "end_meeting", "category": "meetings", "effect": "write", "audience": "host", "description": "End a meeting." },
            { "name": "get_platform_analytics", "category": "platform", "effect": "read", "audience": "platform_staff", "description": "Platform analytics." },
            { "name": "search_knowledge", "category": "knowledge", "effect": "read", "audience": "member", "description": "A category this build has never heard of." }
          ]
        }
        """;

    private readonly IAssistantToolManifestSource _manifestSource = Substitute.For<IAssistantToolManifestSource>();
    private readonly InMemoryPlatformSettingsSource _settings = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginRepository _pluginRepository = Substitute.For<IPluginRepository>();
    private readonly IPluginInstallationRepository _installationRepository = Substitute.For<IPluginInstallationRepository>();
    private readonly IWorkspacePluginToolPolicyRepository _workspaceRuleRepository = Substitute.For<IWorkspacePluginToolPolicyRepository>();
    private readonly List<Plugin> _plugins = [];
    private readonly List<PluginInstallation> _installations = [];
    private readonly List<WorkspacePluginToolPolicy> _workspaceRules = [];
    private bool _callerIsActiveMember = true;

    public AssistantToolsServiceTests()
    {
        _unitOfWork.PluginRepository.Returns(_pluginRepository);
        _unitOfWork.PluginInstallationRepository.Returns(_installationRepository);
        _unitOfWork.WorkspacePluginToolPolicyRepository.Returns(_workspaceRuleRepository);
        _pluginRepository.FindAsync(Arg.Any<Expression<Func<Plugin, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<Plugin>)_plugins
                .Where(call.Arg<Expression<Func<Plugin, bool>>>().Compile()).ToList());
        _installationRepository.FindAsync(Arg.Any<Expression<Func<PluginInstallation, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<PluginInstallation>)_installations
                .Where(call.Arg<Expression<Func<PluginInstallation, bool>>>().Compile()).ToList());
        _workspaceRuleRepository.FindAsync(Arg.Any<Expression<Func<WorkspacePluginToolPolicy, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<WorkspacePluginToolPolicy>)_workspaceRules
                .Where(call.Arg<Expression<Func<WorkspacePluginToolPolicy, bool>>>().Compile()).ToList());
        _manifestSource.GetManifestJsonAsync(Arg.Any<CancellationToken>()).Returns(Manifest);
    }

    // ---- Manifest ---------------------------------------------------------------------------------

    [Fact]
    public async Task ReportsNoManifest_WhenTheKeyIsMissing()
    {
        _manifestSource.GetManifestJsonAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var tools = await GetAsync();

        Assert.False(tools.ManifestAvailable);
        Assert.Null(tools.ManifestGeneratedAt);
        Assert.Empty(tools.BuiltIn);
        Assert.Equal(AssistantToolConstants.WebSearchState.Unknown, tools.WebSearch.State);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"version":1,"webSearch":{"available":true}}""")]
    [InlineData("""{"version":1,"tools":"create_meeting"}""")]
    public async Task ReportsNoManifest_WhenItCannotBeParsed(string stored)
    {
        _manifestSource.GetManifestJsonAsync(Arg.Any<CancellationToken>()).Returns(stored);

        var tools = await GetAsync();

        Assert.False(tools.ManifestAvailable);
        Assert.Null(tools.ManifestGeneratedAt);
        Assert.Empty(tools.BuiltIn);
        Assert.Equal(AssistantToolConstants.WebSearchState.Unknown, tools.WebSearch.State);
    }

    [Fact]
    public async Task PassesTheManifestThrough_IncludingCategoriesItDoesNotKnow()
    {
        var tools = await GetAsync();

        Assert.True(tools.ManifestAvailable);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), tools.ManifestGeneratedAt);
        Assert.Equal(DateTimeKind.Utc, tools.ManifestGeneratedAt!.Value.Kind);
        var create = Assert.Single(tools.BuiltIn, tool => tool.Name == "create_meeting");
        Assert.Equal(new AssistantBuiltInToolDto("create_meeting", "meetings", "write", "member", "Create a meeting."), create);
        // A host tool is listed for everyone; the web adds the host-only note.
        Assert.Contains(tools.BuiltIn, tool => tool.Name == "end_meeting" && tool.Audience == "host");
        Assert.Contains(tools.BuiltIn, tool => tool.Name == "search_knowledge" && tool.Category == "knowledge");
    }

    // ---- platform_staff ---------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ListsPlatformStaffTools_OnlyToPlatformStaff(bool callerIsPlatformStaff)
    {
        var tools = await GetAsync(callerIsPlatformStaff);

        Assert.Equal(callerIsPlatformStaff, tools.BuiltIn.Any(tool => tool.Name == "get_platform_analytics"));
        // Nobody else's tools move.
        Assert.Equal(callerIsPlatformStaff ? 4 : 3, tools.BuiltIn.Count);
    }

    // ---- Web search -------------------------------------------------------------------------------

    [Fact]
    public async Task WebSearchIsOn_WhenTheWorkerHasItAndTheFlagIsAtItsDefault()
    {
        var tools = await GetAsync();

        Assert.Equal(AssistantToolConstants.WebSearchState.On, tools.WebSearch.State);
    }

    [Fact]
    public async Task WebSearchIsOff_WhenThePlatformFlagIsOff()
    {
        _settings.Set(PlatformSettingsCatalog.FlagWarpBotWebSearch, FeatureFlagValue.Off());

        var tools = await GetAsync();

        Assert.Equal(AssistantToolConstants.WebSearchState.Off, tools.WebSearch.State);
    }

    [Fact]
    public async Task WebSearchIsOff_WhenTheFlagDeniesThisWorkspace()
    {
        // The flag's deny list is the only per-workspace switch: read for the workspace, as the
        // worker reads it.
        _settings.Set(
            PlatformSettingsCatalog.FlagWarpBotWebSearch,
            FeatureFlagValue.On() with { DenyWorkspaces = [WorkspaceId.ToString("D")] });

        var tools = await GetAsync();

        Assert.Equal(AssistantToolConstants.WebSearchState.Off, tools.WebSearch.State);
    }

    [Fact]
    public async Task WebSearchIsUnavailable_WhenTheWorkerHasNone_WhateverTheFlagSays()
    {
        _manifestSource.GetManifestJsonAsync(Arg.Any<CancellationToken>())
            .Returns(Manifest.Replace("\"available\": true", "\"available\": false", StringComparison.Ordinal));

        var tools = await GetAsync();

        Assert.True(tools.ManifestAvailable);
        Assert.Equal(AssistantToolConstants.WebSearchState.Unavailable, tools.WebSearch.State);
    }

    // ---- Plugins ----------------------------------------------------------------------------------

    [Fact]
    public async Task GroupsPluginToolsByPlugin_LeavingOutBlockedTools_AndCarryingTheWorkspaceRule()
    {
        Install(Plugin(DrivePluginId, "google_drive", "Google Drive",
                ("google_drive_search", "read"), ("google_drive_export", "read")),
            """{"toolPolicy":{"google_drive_search":"allow"}}""");
        Install(Plugin(CalendarPluginId, "google_calendar", "Google Calendar",
                ("google_calendar_create_event", "write")),
            """{"toolPolicy":{"google_calendar_create_event":"allow"}}""");
        // The Owner blocks one Drive tool and asks every time for the calendar one.
        WorkspaceRule(DrivePluginId, "google_drive_export", PluginConstants.ToolPolicy.Blocked);
        WorkspaceRule(CalendarPluginId, "google_calendar_create_event", PluginConstants.ToolPolicy.Approval);

        var tools = await GetAsync();

        Assert.Equal(["google_calendar", "google_drive"], tools.Plugins.Select(group => group.PluginKey));

        var calendar = tools.Plugins[0];
        Assert.Equal("Google Calendar", calendar.Label);
        var create = Assert.Single(calendar.Tools);
        // The member's own choice, with the Owner's rule beside it rather than folded into it.
        Assert.Equal(PluginConstants.ToolPolicy.Allow, create.Policy);
        Assert.Equal(PluginConstants.ToolPolicy.Approval, create.WorkspacePolicy);
        Assert.Equal("write", create.Effect);

        var drive = tools.Plugins[1];
        var search = Assert.Single(drive.Tools);
        Assert.Equal("google_drive_search", search.Name);
        Assert.Equal("Label for google_drive_search", search.Label);
        Assert.Null(search.WorkspacePolicy);
    }

    [Fact]
    public async Task ListsExactlyWhatTheOrchestratorOffersWarpBot()
    {
        Install(Plugin(DrivePluginId, "google_drive", "Google Drive",
                ("google_drive_search", "read"), ("google_drive_export", "read")),
            """{"toolPolicy":{"google_drive_export":"blocked"}}""");

        var tools = await GetAsync();
        var offered = await Orchestrator().ListAvailableToolsAsync(UserId, WorkspaceId);

        Assert.Equal(
            offered.Value!.Select(tool => (tool.PluginKey, tool.Name)),
            tools.Plugins.SelectMany(group => group.Tools.Select(tool => (group.PluginKey, tool.Name))));
    }

    [Fact]
    public async Task LeavesOutAPluginWhoseEveryToolIsBlocked()
    {
        Install(Plugin(DrivePluginId, "google_drive", "Google Drive", ("google_drive_search", "read")), null);
        WorkspaceRule(DrivePluginId, "google_drive_search", PluginConstants.ToolPolicy.Blocked);

        var tools = await GetAsync();

        Assert.Empty(tools.Plugins);
    }

    [Fact]
    public void SerialisesANullWorkspacePolicy_RatherThanOmittingIt()
    {
        var json = JsonSerializer.Serialize(
            new AssistantPluginToolDto("t", "T", "d", "read", "allow", null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"workspacePolicy\":null", json, StringComparison.Ordinal);
    }

    // ---- Membership -------------------------------------------------------------------------------

    [Fact]
    public async Task RefusesANonMember_WithoutReadingTheManifest()
    {
        _callerIsActiveMember = false;

        var result = await Service().GetToolsAsync(UserId, WorkspaceId, callerIsPlatformStaff: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        await _manifestSource.DidNotReceive().GetManifestJsonAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Controller_Answers403_ForANonMember()
    {
        _callerIsActiveMember = false;

        var response = await Controller(Service(), StaffAccess(isStaff: false))
            .GetTools(WorkspaceId, CancellationToken.None);

        var refused = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Controller_DecidesPlatformStaffWithTheStaffCheck(bool isStaff)
    {
        var service = Substitute.For<IAssistantToolsService>();
        service.GetToolsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AssistantToolsDto(false, null, [], new AssistantWebSearchDto("unknown"), [])));

        var response = await Controller(service, StaffAccess(isStaff), staffHint: true)
            .GetTools(WorkspaceId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        await service.Received(1).GetToolsAsync(UserId, WorkspaceId, isStaff, Arg.Any<CancellationToken>());
    }

    // ---- Manifest source ----------------------------------------------------------------------------

    [Fact]
    public async Task ManifestSource_CachesTheReadForAMinute_AndSwallowsRedisFailures()
    {
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(Manifest));
        var clock = new ManualClock();
        var source = new RedisAssistantToolManifestSource(
            redis, NullLogger<RedisAssistantToolManifestSource>.Instance, clock);

        Assert.Equal(Manifest, await source.GetManifestJsonAsync());
        Assert.Equal(Manifest, await source.GetManifestJsonAsync());
        await database.Received(1).StringGetAsync(
            Arg.Is<RedisKey>(key => key == AssistantToolConstants.ManifestRedisKey), Arg.Any<CommandFlags>());

        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        clock.Advance(RedisAssistantToolManifestSource.CacheTtl);

        Assert.Null(await source.GetManifestJsonAsync());
    }

    // ---- Fixture ------------------------------------------------------------------------------------

    private async Task<AssistantToolsDto> GetAsync(bool callerIsPlatformStaff = false)
    {
        var result = await Service().GetToolsAsync(UserId, WorkspaceId, callerIsPlatformStaff);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    private AssistantToolsService Service() => new(
        _manifestSource,
        TestWorkspacePluginPolicy.Guard(allowsPluginUsage: true, isActiveMember: _callerIsActiveMember),
        Orchestrator(),
        new PlatformSettingsReader(_settings, NullLogger<PlatformSettingsReader>.Instance));

    private McpToolOrchestrator Orchestrator() => new(
        new TestPluginProviderResolver(Substitute.For<IMcpToolGateway>()),
        _unitOfWork,
        TestWorkspacePluginPolicy.Guard(allowsPluginUsage: true, isActiveMember: _callerIsActiveMember),
        Substitute.For<IPluginTokenRefresher>(),
        Substitute.For<IMcpConfirmationTokenService>());

    private static AssistantToolsController Controller(
        IAssistantToolsService service,
        IStaffAccessResolver staffAccess,
        bool staffHint = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };
        if (staffHint) claims.Add(new Claim(ClaimTypes.Role, StaffClaims.StaffRoleHint));

        return new AssistantToolsController(service, staffAccess)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                },
            },
        };
    }

    private static IStaffAccessResolver StaffAccess(bool isStaff)
    {
        var resolver = Substitute.For<IStaffAccessResolver>();
        resolver.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), AdminPermissions.WarpBotUse, Arg.Any<CancellationToken>())
            .Returns(isStaff);
        return resolver;
    }

    private void Install(Plugin plugin, string? configJson)
    {
        _plugins.Add(plugin);
        _installations.Add(new PluginInstallation
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            PluginId = plugin.Id,
            Status = PluginConstants.InstallationStatus.Installed,
            InstalledAt = DateTime.UtcNow,
            ConnectedAt = DateTime.UtcNow,
            ConfigJson = configJson,
        });
    }

    private void WorkspaceRule(Guid pluginId, string toolName, string policy) =>
        _workspaceRules.Add(new WorkspacePluginToolPolicy
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            PluginId = pluginId,
            ToolName = toolName,
            Policy = policy,
            SetBy = Guid.NewGuid(),
            SetAt = DateTime.UtcNow,
        });

    private static Plugin Plugin(Guid id, string key, string label, params (string Name, string Effect)[] tools)
    {
        var toolsJson = JsonSerializer.Serialize(tools.Select(tool => new
        {
            name = tool.Name,
            pluginKey = key,
            label = $"Label for {tool.Name}",
            description = $"Description of {tool.Name}.",
            effect = tool.Effect,
            requiredScopes = Array.Empty<string>(),
            parameters = new { type = "object", properties = new { } },
        }));

        return new Plugin
        {
            Id = id,
            PluginKey = key,
            Label = label,
            Description = label,
            Provider = PluginConstants.Providers.Google,
            Kind = PluginConstants.PluginKind.Native,
            OAuthClientSource = "preregistered",
            IsActive = true,
            RequiredScopesJson = "[]",
            ToolsJson = toolsJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
