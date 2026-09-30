using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// A plugin the PLATFORM turned off for a workspace is off on every surface there - the Owner's
/// marketplace, requests, install, connect, the member's catalog and WarpBot's tool list - while
/// members' connections are left exactly as they were.
/// </summary>
/// <remarks>
/// One world for all of it: a curated workspace whose Owner added Linear and Notion, and a member
/// who installed and connected both. Real services over in-memory repositories and the REAL guard,
/// so what is asserted is the whole path, not a stubbed verdict.
/// </remarks>
public class PluginPlatformEnforcementTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OwnerId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid MemberId = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid NewMemberId = Guid.Parse("dddddddd-0000-0000-0000-000000000003");

    private readonly List<Plugin> _plugins = [];
    private readonly List<WorkspacePlugin> _workspacePlugins = [];
    private readonly List<WorkspacePluginCuration> _curations = [];
    private readonly List<WorkspacePluginOverride> _overrides = [];
    private readonly List<PluginRequest> _requests = [];
    private readonly List<PluginInstallation> _installations = [];
    private readonly List<PluginConnection> _connections = [];

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspacePluginPolicyClient _policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
    private readonly IWorkspaceMembershipClient _membershipClient = Substitute.For<IWorkspaceMembershipClient>();
    private readonly IWorkspaceDirectoryClient _directoryClient = Substitute.For<IWorkspaceDirectoryClient>();

    private readonly Plugin _linear;
    private readonly Plugin _notion;

    public PluginPlatformEnforcementTests()
    {
        var plugins = InMemoryRepository.Create<IPluginRepository, Plugin>(_plugins, p => p.Id);
        var workspacePlugins = InMemoryRepository.Create<IWorkspacePluginRepository, WorkspacePlugin>(_workspacePlugins, r => r.Id);
        var curations = InMemoryRepository.Create<IWorkspacePluginCurationRepository, WorkspacePluginCuration>(_curations, c => c.WorkspaceId);
        var overrides = InMemoryRepository.Create<IWorkspacePluginOverrideRepository, WorkspacePluginOverride>(_overrides, o => o.Id);
        var requests = InMemoryRepository.Create<IPluginRequestRepository, PluginRequest>(_requests, r => r.Id);
        requests.ListForWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<PluginRequest>)_requests
                .Where(r => r.WorkspaceId == call.ArgAt<Guid>(0) && r.Status == call.ArgAt<string>(1))
                .ToList());
        var installations = InMemoryRepository.Create<IPluginInstallationRepository, PluginInstallation>(_installations, i => i.Id);
        var connections = InMemoryRepository.Create<IPluginConnectionRepository, PluginConnection>(_connections, c => c.Id);
        _unitOfWork.PluginRepository.Returns(plugins);
        _unitOfWork.WorkspacePluginRepository.Returns(workspacePlugins);
        _unitOfWork.WorkspacePluginCurationRepository.Returns(curations);
        _unitOfWork.WorkspacePluginOverrideRepository.Returns(overrides);
        _unitOfWork.PluginRequestRepository.Returns(requests);
        _unitOfWork.PluginInstallationRepository.Returns(installations);
        _unitOfWork.PluginConnectionRepository.Returns(connections);
        var audits = Substitute.For<IPluginToolAuditRepository>();
        audits.GetPluginIdsUsedInWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        audits.CountDistinctUsersByPluginForWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _unitOfWork.PluginToolAuditRepository.Returns(audits);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        _membershipClient.GetMembershipAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.ArgAt<Guid>(0) != WorkspaceId) return WorkspaceMembership.None;
                var user = call.ArgAt<Guid>(1);
                if (user == OwnerId) return new WorkspaceMembership(true, "Owner", true);
                if (user == MemberId || user == NewMemberId) return new WorkspaceMembership(true, "Member", true);
                return WorkspaceMembership.None;
            });
        _directoryClient.GetProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new WorkspaceProfile(call.Arg<Guid>(), "Demo", "demo", OwnerId));

        _linear = WithTool(WorkspacePluginGuardTests.Marketplace("linear"), "linear_search");
        _notion = WithTool(WorkspacePluginGuardTests.Marketplace("notion"), "notion_search");
        _plugins.AddRange([_linear, _notion]);

        // The Owner added both, and the member connected both.
        _curations.Add(new WorkspacePluginCuration { WorkspaceId = WorkspaceId, CuratedAt = DateTime.UtcNow, CuratedBy = OwnerId });
        foreach (var plugin in _plugins)
        {
            _workspacePlugins.Add(new WorkspacePlugin
            {
                Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, PluginId = plugin.Id, AddedBy = OwnerId, AddedAt = DateTime.UtcNow,
            });
            _installations.Add(new PluginInstallation
            {
                Id = Guid.NewGuid(), UserId = MemberId, PluginId = plugin.Id,
                Status = PluginConstants.InstallationStatus.Installed, InstalledAt = DateTime.UtcNow, ConnectedAt = DateTime.UtcNow,
            });
            _connections.Add(new PluginConnection
            {
                Id = Guid.NewGuid(), UserId = MemberId, PluginId = plugin.Id, Provider = plugin.Provider,
                Status = PluginConstants.ConnectionStatus.Connected,
                EncryptedAccessToken = "protected-access", EncryptedRefreshToken = "protected-refresh",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
    }

    // ---- the guard -----------------------------------------------------------------------------

    [Fact]
    public async Task ADisablingOverride_BeatsTheOwnersOwnList()
    {
        DisableHere(_linear);

        var linear = await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, MemberId, _linear);
        var notion = await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, MemberId, _notion);

        Assert.False(linear.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, linear.ErrorCode);
        Assert.Equal(PluginWorkspaceAccessConstants.Messages.DisabledByPlatform, linear.Error);
        Assert.True(notion.IsSuccess);
    }

    [Fact]
    public async Task AnOptInPlugin_IsOffEvenOnTheList_UntilThePlatformEnablesItHere()
    {
        _linear.WorkspaceDefault = PluginWorkspaceAccessConstants.Default.OptIn;

        Assert.False((await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, MemberId, _linear)).IsSuccess);

        Override(_linear, PluginWorkspaceAccessConstants.OverrideState.Enabled);

        Assert.True((await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, MemberId, _linear)).IsSuccess);
    }

    [Theory]
    [InlineData("business", true)]
    [InlineData("free", false)]
    [InlineData(null, false)]
    public async Task APlanRule_LetsOnlyTheNamedPlansThrough(string? plan, bool usable)
    {
        _linear.AllowedPlanSlugsJson = """["business","enterprise"]""";
        _policyClient.ReadPlanSlugAsync(WorkspaceId, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, MemberId, _linear);

        Assert.Equal(usable, result.IsSuccess);
    }

    [Fact]
    public async Task ThePlanIsNotReadAtAll_WhileNoPluginHasAPlanRule()
    {
        // The common case - no plan rules anywhere - must not add a workspace-service round trip to
        // every tool call and catalog listing.
        await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, MemberId, _linear);

        await _policyClient.DidNotReceive().ReadPlanSlugAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ---- WarpBot's tools -----------------------------------------------------------------------

    [Fact]
    public async Task WarpBot_IsNotOfferedAPlatformDisabledPluginsTools_AndTheConnectionIsLeftAlone()
    {
        DisableHere(_linear);

        var result = await Orchestrator().ListAvailableToolsAsync(MemberId, WorkspaceId);

        Assert.True(result.IsSuccess);
        var tool = Assert.Single(result.Value!);
        Assert.Equal("notion_search", tool.Name);

        // Inert, not deleted: the grant is exactly as it was, so enabling the plugin again restores it.
        var connection = _connections.Single(c => c.PluginId == _linear.Id);
        Assert.Equal(PluginConstants.ConnectionStatus.Connected, connection.Status);
        Assert.Equal("protected-access", connection.EncryptedAccessToken);
        Assert.Equal("protected-refresh", connection.EncryptedRefreshToken);
        Assert.NotNull(_installations.Single(i => i.PluginId == _linear.Id).ConnectedAt);
    }

    // ---- the member's catalog, install and connect ------------------------------------------------

    [Fact]
    public async Task TheMembersCatalog_KeepsTheirConnectedRow_MarkedAndExplained()
    {
        DisableHere(_linear);

        var items = (await Installations().ListCatalogAsync(MemberId, WorkspaceId)).Value!;

        var linear = items.Single(i => i.Key == "linear");
        Assert.Equal(WorkspacePluginConstants.Availability.DisabledByPlatform, linear.WorkspaceAvailability);
        Assert.Equal(PluginWorkspaceAccessConstants.Messages.DisabledByPlatform, linear.WorkspacePolicyBlockReason);
        Assert.False(linear.CanAdd);
    }

    [Fact]
    public async Task TheCatalog_HidesAPlatformDisabledPlugin_FromAMemberWhoNeverInstalledIt()
    {
        DisableHere(_linear);

        var items = (await Installations().ListCatalogAsync(NewMemberId, WorkspaceId)).Value!;

        Assert.DoesNotContain(items, i => i.Key == "linear");
        Assert.Contains(items, i => i.Key == "notion");
    }

    [Fact]
    public async Task Installing_APlatformDisabledPlugin_InThatWorkspace_IsRefused()
    {
        DisableHere(_linear);

        var result = await Installations().InstallAsync("linear", NewMemberId, WorkspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.DoesNotContain(_installations, i => i.UserId == NewMemberId);
    }

    [Fact]
    public async Task Connecting_APlatformDisabledPlugin_InThatWorkspace_IsRefused_BeforeAnyProviderIsCalled()
    {
        DisableHere(_linear);
        var providers = Substitute.For<IPluginProviderResolver>();

        var result = await Connections(providers).GetConnectUrlAsync("linear", MemberId, workspaceId: WorkspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.Equal(PluginWorkspaceAccessConstants.Messages.DisabledByPlatform, result.Error);
        Assert.Empty(providers.ReceivedCalls());
    }

    // ---- the Owner's marketplace ---------------------------------------------------------------

    [Fact]
    public async Task TheOwnersPage_DoesNotOfferIt_ButShowsWhereItWent()
    {
        var jira = WorkspacePluginGuardTests.Marketplace("jira");
        _plugins.Add(jira);
        DisableHere(_linear);
        DisableHere(jira);

        var overview = (await Marketplace().GetOverviewAsync(WorkspaceId, OwnerId)).Value!;

        Assert.DoesNotContain(overview.InWorkspace, i => i.Key == "linear");
        Assert.DoesNotContain(overview.Marketplace, i => i.Key is "linear" or "jira");
        // Linear was on the list, so it is shown as turned off; Jira never was, so it simply is not there.
        var shown = Assert.Single(overview.DisabledByPlatform);
        Assert.Equal("linear", shown.Key);
        Assert.Equal(WorkspacePluginConstants.Availability.DisabledByPlatform, shown.Availability);
    }

    [Fact]
    public async Task TheOwner_CannotAddAPlatformDisabledPlugin_And409Says()
    {
        var jira = WorkspacePluginGuardTests.Marketplace("jira");
        _plugins.Add(jira);
        DisableHere(jira);

        var result = await Marketplace().AddMarketplacePluginAsync(WorkspaceId, OwnerId, "jira");

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginWorkspaceAccessConstants.ErrorCodes.DisabledByPlatform, result.ErrorCode);
        Assert.Equal(StatusCodes.Status409Conflict, WorkspacePluginsController.StatusFor(result.ErrorCode));
        Assert.DoesNotContain(_workspacePlugins, r => r.PluginId == jira.Id);
    }

    [Fact]
    public async Task AMember_CannotAskForAPlatformDisabledPlugin_AndLearnsNothingAboutIt()
    {
        var jira = WorkspacePluginGuardTests.Marketplace("jira");
        _plugins.Add(jira);
        DisableHere(jira);

        var result = await Marketplace().CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest("jira"));

        Assert.Equal(PluginConstants.ErrorCodes.UnknownPlugin, result.ErrorCode);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task ApprovingARequest_ForAPluginThePlatformTurnedOffSince_DeclinesIt()
    {
        var jira = WorkspacePluginGuardTests.Marketplace("jira");
        _plugins.Add(jira);
        var request = new PluginRequest
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, PluginId = jira.Id, RequestedBy = MemberId,
            Status = WorkspacePluginConstants.RequestStatus.Pending, CreatedAt = DateTime.UtcNow,
        };
        _requests.Add(request);
        DisableHere(jira);

        var result = await Marketplace().ApproveRequestAsync(WorkspaceId, OwnerId, request.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspacePluginConstants.RequestStatus.Declined, request.Status);
        Assert.DoesNotContain(_workspacePlugins, r => r.PluginId == jira.Id);
    }

    // ---- plumbing ------------------------------------------------------------------------------

    private void DisableHere(Plugin plugin) =>
        Override(plugin, PluginWorkspaceAccessConstants.OverrideState.Disabled, "security review");

    private void Override(Plugin plugin, string state, string? reason = null) =>
        _overrides.Add(new WorkspacePluginOverride
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, PluginId = plugin.Id, State = state, Reason = reason, SetAt = DateTime.UtcNow,
        });

    private WorkspacePluginGuard Guard() => new(_unitOfWork, _policyClient, _membershipClient);

    private McpToolOrchestrator Orchestrator() => new(
        Substitute.For<IPluginProviderResolver>(),
        _unitOfWork,
        Guard(),
        Substitute.For<IPluginTokenRefresher>(),
        Substitute.For<IMcpConfirmationTokenService>());

    private PluginInstallationService Installations() => new(
        _unitOfWork,
        Substitute.For<IPluginCredentialProtector>(),
        Guard(),
        Substitute.For<IAdminAuditRecorder>());

    private PluginConnectionService Connections(IPluginProviderResolver providers) => new(
        _unitOfWork,
        providers,
        Substitute.For<IPluginOAuthStateProtector>(),
        Substitute.For<IPluginCredentialProtector>(),
        NullLogger<PluginConnectionService>.Instance,
        Substitute.For<IMcpClientProvisioner>(),
        Guard());

    private WorkspacePluginMarketplaceService Marketplace() => new(
        _unitOfWork,
        Guard(),
        _policyClient,
        _membershipClient,
        _directoryClient,
        Substitute.For<IUserNotificationClient>(),
        NullLogger<WorkspacePluginMarketplaceService>.Instance);

    private static Plugin WithTool(Plugin plugin, string toolName)
    {
        plugin.ToolsJson = $$"""
            [
              {
                "name": "{{toolName}}",
                "pluginKey": "{{plugin.PluginKey}}",
                "label": "{{toolName}}",
                "description": "Search.",
                "effect": "read",
                "requiredScopes": [],
                "parameters": { "type": "object", "properties": { "query": { "type": "string" } } }
              }
            ]
            """;
        return plugin;
    }
}
