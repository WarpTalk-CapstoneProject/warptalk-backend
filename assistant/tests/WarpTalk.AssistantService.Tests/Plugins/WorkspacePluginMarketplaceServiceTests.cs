using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The workspace plugin marketplace: authorization, the AllowAnyPlugins transition, private plugins
/// and the request round trip. Repositories are in-memory lists that apply the predicates they are
/// handed, so the queries' own filters are under test rather than stubbed away.
/// </summary>
public class WorkspacePluginMarketplaceServiceTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherWorkspaceId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid OwnerId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid AdminId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid MemberId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly Guid OutsiderId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");

    private readonly List<Plugin> _plugins = [];
    private readonly List<WorkspacePlugin> _workspacePlugins = [];
    private readonly List<WorkspacePluginCuration> _curations = [];
    private readonly List<PluginRequest> _requests = [];
    private readonly List<UserNotification> _sent = [];

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspacePluginPolicyClient _policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
    private readonly IWorkspaceMembershipClient _membershipClient = Substitute.For<IWorkspaceMembershipClient>();
    private readonly IWorkspaceDirectoryClient _directoryClient = Substitute.For<IWorkspaceDirectoryClient>();
    private readonly IUserNotificationClient _notificationClient = Substitute.For<IUserNotificationClient>();

    private readonly Plugin _linear;
    private readonly Plugin _notion;
    private readonly Plugin _slackRetired;

    public WorkspacePluginMarketplaceServiceTests()
    {
        var pluginRepository = InMemory<IPluginRepository, Plugin>(_plugins, p => p.Id);
        var workspacePluginRepository = InMemory<IWorkspacePluginRepository, WorkspacePlugin>(_workspacePlugins, r => r.Id);
        var curationRepository = InMemory<IWorkspacePluginCurationRepository, WorkspacePluginCuration>(_curations, c => c.WorkspaceId);
        var requestRepository = InMemory<IPluginRequestRepository, PluginRequest>(_requests, r => r.Id);
        requestRepository.ListForWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<PluginRequest>)_requests
                .Where(r => r.WorkspaceId == call.ArgAt<Guid>(0) && r.Status == call.ArgAt<string>(1))
                .OrderBy(r => r.CreatedAt)
                .ToList());
        var connectionRepository = Substitute.For<IPluginConnectionRepository>();
        var auditRepository = Substitute.For<IPluginToolAuditRepository>();
        auditRepository.CountDistinctUsersByPluginForWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());

        _unitOfWork.PluginRepository.Returns(pluginRepository);
        _unitOfWork.WorkspacePluginRepository.Returns(workspacePluginRepository);
        _unitOfWork.WorkspacePluginCurationRepository.Returns(curationRepository);
        _unitOfWork.PluginRequestRepository.Returns(requestRepository);
        _unitOfWork.PluginConnectionRepository.Returns(connectionRepository);
        _unitOfWork.PluginToolAuditRepository.Returns(auditRepository);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        _membershipClient.GetMembershipAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.ArgAt<Guid>(0) != WorkspaceId) return WorkspaceMembership.None;
                var user = call.ArgAt<Guid>(1);
                if (user == OwnerId) return new WorkspaceMembership(true, "Owner", true);
                if (user == AdminId) return new WorkspaceMembership(true, "Admin", true);
                if (user == MemberId) return new WorkspaceMembership(true, "Member", true);
                return WorkspaceMembership.None;
            });
        _directoryClient.GetProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new WorkspaceProfile(call.Arg<Guid>(), "WarpTalk Demo", "warptalk-demo", OwnerId));
        _notificationClient.SendAsync(Arg.Any<UserNotification>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _sent.Add(call.Arg<UserNotification>());
                return true;
            });

        _linear = WorkspacePluginGuardTests.Marketplace("linear");
        _notion = WorkspacePluginGuardTests.Marketplace("notion");
        _slackRetired = WorkspacePluginGuardTests.Marketplace("slack");
        _slackRetired.IsActive = false;
        _plugins.AddRange([_linear, _notion, _slackRetired]);

        AllowAnyPlugins(true);
    }

    private WorkspacePluginMarketplaceService Sut()
    {
        var guard = new WorkspacePluginGuard(_unitOfWork, _policyClient, _membershipClient);
        return new WorkspacePluginMarketplaceService(
            _unitOfWork,
            guard,
            _policyClient,
            _membershipClient,
            _directoryClient,
            _notificationClient,
            NullLogger<WorkspacePluginMarketplaceService>.Instance);
    }

    // ---- authorization -------------------------------------------------------------------------

    [Fact]
    public async Task ANonMember_IsRefusedEverything_WithoutLearningAnything()
    {
        var sut = Sut();

        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, (await sut.GetOverviewAsync(WorkspaceId, OutsiderId)).ErrorCode);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, (await sut.ListMyRequestsAsync(WorkspaceId, OutsiderId)).ErrorCode);
        Assert.Equal(
            PluginConstants.ErrorCodes.PermissionDenied,
            (await sut.CreateRequestAsync(WorkspaceId, OutsiderId, null, new CreatePluginRequestRequest("linear"))).ErrorCode);
        Assert.Equal(
            PluginConstants.ErrorCodes.PermissionDenied,
            (await sut.AddMarketplacePluginAsync(WorkspaceId, OutsiderId, "linear")).ErrorCode);
        Assert.Empty(_requests);
        Assert.Empty(_workspacePlugins);
    }

    [Fact]
    public async Task AMember_CannotAddRemoveOrReadTheWorkspaceList()
    {
        AllowAnyPlugins(false);
        var sut = Sut();

        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, (await sut.AddMarketplacePluginAsync(WorkspaceId, MemberId, "linear")).ErrorCode);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, (await sut.RemovePluginAsync(WorkspaceId, MemberId, "linear")).ErrorCode);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, (await sut.GetOverviewAsync(WorkspaceId, MemberId)).ErrorCode);
        Assert.Equal(
            PluginConstants.ErrorCodes.PermissionDenied,
            (await sut.CreatePrivatePluginAsync(WorkspaceId, MemberId, new CreatePrivatePluginRequest("CRM", "https://crm.example.com/mcp"))).ErrorCode);
        Assert.Empty(_workspacePlugins);
        Assert.Empty(_curations);
    }

    [Fact]
    public async Task AnAdmin_ReadsButDoesNotWrite()
    {
        AllowAnyPlugins(false);
        var sut = Sut();

        var overview = await sut.GetOverviewAsync(WorkspaceId, AdminId);
        Assert.True(overview.IsSuccess);
        Assert.False(overview.Value!.CanManage);

        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, (await sut.AddMarketplacePluginAsync(WorkspaceId, AdminId, "linear")).ErrorCode);
    }

    [Fact]
    public async Task TheOwner_CanAdd_AndTheWorkspaceBecomesCurated()
    {
        AllowAnyPlugins(false);

        var result = await Sut().AddMarketplacePluginAsync(WorkspaceId, OwnerId, "linear");

        Assert.True(result.IsSuccess);
        var row = Assert.Single(_workspacePlugins);
        Assert.Equal(_linear.Id, row.PluginId);
        Assert.Equal(OwnerId, row.AddedBy);
        Assert.Single(_curations);
    }

    [Fact]
    public async Task ARetiredPlugin_CannotBeNewlyAdded()
    {
        AllowAnyPlugins(false);

        var result = await Sut().AddMarketplacePluginAsync(WorkspaceId, OwnerId, "slack");

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.PluginRetired, result.ErrorCode);
        Assert.Empty(_workspacePlugins);
    }

    // ---- the transition ------------------------------------------------------------------------

    [Fact]
    public async Task Transition_AnUncuratedWorkspaceOnAllowAnyPlugins_KeepsEveryOtherPlugin_WhenTheOwnerRemovesOne()
    {
        // The migration-safety guarantee: the Owner's first change is the ONLY change. Removing
        // Linear from a workspace that had every plugin leaves it with every other active one.
        AllowAnyPlugins(true);

        var result = await Sut().RemovePluginAsync(WorkspaceId, OwnerId, "linear");

        Assert.True(result.IsSuccess);
        var curation = Assert.Single(_curations);
        Assert.True(curation.SeededFromAllowAnyPlugins);
        var remaining = _workspacePlugins.Select(r => r.PluginId).ToList();
        Assert.Equal([_notion.Id], remaining);
        Assert.All(_workspacePlugins, r => Assert.Null(r.AddedBy));
    }

    [Fact]
    public async Task Transition_AnUncuratedWorkspace_ReadsAsHavingEveryMarketplacePlugin()
    {
        AllowAnyPlugins(true);

        var overview = (await Sut().GetOverviewAsync(WorkspaceId, OwnerId)).Value!;

        Assert.False(overview.IsCurated);
        Assert.Equal(["linear", "notion"], overview.InWorkspace.Select(p => p.Key).Order());
        Assert.Empty(overview.Marketplace);
        // Reading never curates: only a write does.
        Assert.Empty(_curations);
    }

    [Fact]
    public async Task Transition_WithAllowAnyPluginsOff_SeedsNothing()
    {
        AllowAnyPlugins(false);

        await Sut().AddMarketplacePluginAsync(WorkspaceId, OwnerId, "notion");

        Assert.False(Assert.Single(_curations).SeededFromAllowAnyPlugins);
        Assert.Equal([_notion.Id], _workspacePlugins.Select(r => r.PluginId));
    }

    [Fact]
    public async Task Transition_WhenAllowAnyPluginsCannotBeRead_TheFirstEditChangesNothing()
    {
        // Gap 1 of the marketplace audit. The policy client used to answer an outage with "off",
        // and the first edit wrote that down: a curation row with nothing seeded, i.e. every member
        // of a workspace that had every plugin permanently left with the one the Owner clicked.
        PolicyUnreadable();
        var sut = Sut();

        var add = await sut.AddMarketplacePluginAsync(WorkspaceId, OwnerId, "linear");
        var remove = await sut.RemovePluginAsync(WorkspaceId, OwnerId, "notion");

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.PolicyUnavailable, add.ErrorCode);
        Assert.Equal(WorkspacePluginConstants.ErrorCodes.PolicyUnavailable, remove.ErrorCode);
        Assert.Empty(_curations);
        Assert.Empty(_workspacePlugins);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Transition_ApprovingDuringAnOutage_LeavesTheRequestPending()
    {
        AllowAnyPlugins(false);
        var sut = Sut();
        var request = (await sut.CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest("linear"))).Value!;
        _sent.Clear();
        PolicyUnreadable();

        var result = await sut.ApproveRequestAsync(WorkspaceId, OwnerId, request.Id);

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.PolicyUnavailable, result.ErrorCode);
        Assert.Equal(WorkspacePluginConstants.RequestStatus.Pending, Assert.Single(_requests).Status);
        Assert.Empty(_curations);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task ACuratedWorkspace_DoesNotNeedThePolicy_ToChangeItsList()
    {
        // Only the transition reads the switch; once the list is the list, an outage is irrelevant.
        AlreadyCurated();
        PolicyUnreadable();

        var result = await Sut().AddMarketplacePluginAsync(WorkspaceId, OwnerId, "linear");

        Assert.True(result.IsSuccess);
        Assert.Equal([_linear.Id], _workspacePlugins.Select(r => r.PluginId));
        await _policyClient.DidNotReceive().ReadAllowAnyPluginsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Transition_RetiredPluginsAreNotSeeded()
    {
        AllowAnyPlugins(true);

        await Sut().AddMarketplacePluginAsync(WorkspaceId, OwnerId, "linear");

        Assert.DoesNotContain(_workspacePlugins, r => r.PluginId == _slackRetired.Id);
        Assert.Equal(OwnerId, _workspacePlugins.Single(r => r.PluginId == _linear.Id).AddedBy);
    }

    // ---- requests ------------------------------------------------------------------------------

    [Fact]
    public async Task AMember_CanRequest_AndTheOwnerIsNotified()
    {
        AllowAnyPlugins(false);

        var result = await Sut().CreateRequestAsync(
            WorkspaceId, MemberId, "ky@warptalk.io.vn", new CreatePluginRequestRequest("linear", "  Issues from action items  "));

        Assert.True(result.IsSuccess);
        var request = Assert.Single(_requests);
        Assert.Equal("Issues from action items", request.Reason);
        Assert.Equal(WorkspacePluginConstants.RequestStatus.Pending, request.Status);

        var notification = Assert.Single(_sent);
        Assert.Equal(OwnerId, notification.UserId);
        Assert.Equal(WorkspacePluginConstants.NotificationTypes.Requested, notification.Type);
        Assert.Equal("/warptalk-demo/settings/plugins", notification.ActionUrl);
        Assert.Equal(
            ["plugin_key", "plugin_label", "request_id", "workspace_id", "workspace_name"],
            notification.Metadata.Keys.Order());
    }

    [Fact]
    public async Task ADuplicatePendingRequest_IsAConflict()
    {
        AllowAnyPlugins(false);
        var sut = Sut();

        await sut.CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest("linear"));
        var second = await sut.CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest("linear"));

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.RequestAlreadyPending, second.ErrorCode);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task RequestingAPluginTheWorkspaceAlreadyHas_IsAConflict()
    {
        AllowAnyPlugins(true);

        var result = await Sut().CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest("linear"));

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.PluginAlreadyAvailable, result.ErrorCode);
    }

    [Fact]
    public async Task AReasonOverTheLimit_IsRefused()
    {
        AllowAnyPlugins(false);

        var result = await Sut().CreateRequestAsync(
            WorkspaceId, MemberId, null, new CreatePluginRequestRequest("linear", new string('x', 501)));

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.InvalidRequest, result.ErrorCode);
    }

    [Fact]
    public async Task AnotherWorkspacesPrivatePlugin_CannotBeRequested()
    {
        AllowAnyPlugins(false);
        var foreign = WorkspacePluginGuardTests.Private("ws_crm_00000000", OtherWorkspaceId);
        _plugins.Add(foreign);

        var result = await Sut().CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest(foreign.PluginKey));

        Assert.Equal(PluginConstants.ErrorCodes.UnknownPlugin, result.ErrorCode);
    }

    [Fact]
    public async Task Approving_AddsThePlugin_AnswersEveryPendingRequestForIt_AndNotifiesEachRequester()
    {
        AllowAnyPlugins(false);
        var sut = Sut();
        var first = (await sut.CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest("linear"))).Value!;
        await sut.CreateRequestAsync(WorkspaceId, AdminId, null, new CreatePluginRequestRequest("linear"));
        _sent.Clear();

        var result = await sut.ApproveRequestAsync(WorkspaceId, OwnerId, first.Id);

        Assert.True(result.IsSuccess);
        Assert.Contains(_workspacePlugins, r => r.PluginId == _linear.Id);
        Assert.All(_requests, r =>
        {
            Assert.Equal(WorkspacePluginConstants.RequestStatus.Approved, r.Status);
            Assert.Equal(OwnerId, r.DecidedBy);
            Assert.NotNull(r.DecidedAt);
        });
        Assert.Equal([AdminId, MemberId], _sent.Select(n => n.UserId).Order());
        Assert.All(_sent, n => Assert.Equal(WorkspacePluginConstants.NotificationTypes.RequestApproved, n.Type));
    }

    [Fact]
    public async Task Declining_NotifiesTheRequester_AndAddsNothing()
    {
        AllowAnyPlugins(false);
        var sut = Sut();
        var request = (await sut.CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest("linear"))).Value!;
        _sent.Clear();

        var result = await sut.DeclineRequestAsync(WorkspaceId, OwnerId, request.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(_workspacePlugins);
        var notification = Assert.Single(_sent);
        Assert.Equal(MemberId, notification.UserId);
        Assert.Equal(WorkspacePluginConstants.NotificationTypes.RequestDeclined, notification.Type);

        var again = await sut.DeclineRequestAsync(WorkspaceId, OwnerId, request.Id);
        Assert.Equal(WorkspacePluginConstants.ErrorCodes.RequestNotPending, again.ErrorCode);
    }

    [Fact]
    public async Task ARequestFromAnotherWorkspace_IsUnknownHere()
    {
        _requests.Add(new PluginRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = OtherWorkspaceId,
            PluginId = _linear.Id,
            RequestedBy = OutsiderId,
            Status = WorkspacePluginConstants.RequestStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        });

        var result = await Sut().ApproveRequestAsync(WorkspaceId, OwnerId, _requests[0].Id);

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.UnknownRequest, result.ErrorCode);
        Assert.Empty(_workspacePlugins);
    }

    // ---- races ---------------------------------------------------------------------------------

    [Fact]
    public async Task TwoFirstEditsAtOnce_TheLoserGetsA409_NotA500()
    {
        // Gap 6. Both edits found no curation row and both inserted one; the primary key refused the
        // second. The data is already right - the answer has to be "refetch".
        AllowAnyPlugins(false);
        var request = (await Sut().CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest("linear"))).Value!;
        _sent.Clear();
        SavesLoseARace();

        // Approve first: the in-memory store applies a staged change before the save is refused,
        // which a real rolled-back transaction would not.
        var approve = await Sut().ApproveRequestAsync(WorkspaceId, OwnerId, request.Id);
        var add = await Sut().AddMarketplacePluginAsync(WorkspaceId, OwnerId, "linear");
        var remove = await Sut().RemovePluginAsync(WorkspaceId, OwnerId, "notion");

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.ListChangedConcurrently, add.ErrorCode);
        Assert.Equal(WorkspacePluginConstants.ErrorCodes.ListChangedConcurrently, remove.ErrorCode);
        Assert.Equal(WorkspacePluginConstants.ErrorCodes.ListChangedConcurrently, approve.ErrorCode);
        // Nothing committed, so nobody is told it was added.
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task ADoubleClickedRequest_TheLoserIsAlreadyPending_NotA500()
    {
        AllowAnyPlugins(false);
        SavesLoseARace();

        var result = await Sut().CreateRequestAsync(WorkspaceId, MemberId, null, new CreatePluginRequestRequest("linear"));

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.RequestAlreadyPending, result.ErrorCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AFailureThatIsNotAUniqueViolation_StillThrows()
    {
        // Narrow on purpose: a foreign-key or connection failure is a fault, not a race to explain.
        AllowAnyPlugins(false);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new DbUpdateException("fk", new FakeDbException("23503")));

        await Assert.ThrowsAsync<DbUpdateException>(() => Sut().AddMarketplacePluginAsync(WorkspaceId, OwnerId, "linear"));
    }

    // ---- private plugins -----------------------------------------------------------------------

    [Fact]
    public async Task TheOwner_CreatesAPrivatePlugin_WithADerivedKey_VisibleOnlyInThatWorkspace()
    {
        var result = await Sut().CreatePrivatePluginAsync(
            WorkspaceId, OwnerId, new CreatePrivatePluginRequest("Internal CRM", "https://crm.warptalk.io.vn/mcp", "Customer lookups"));

        Assert.True(result.IsSuccess);
        var plugin = _plugins.Single(p => p.OwnerWorkspaceId == WorkspaceId);
        Assert.StartsWith("ws_internal_crm_", plugin.PluginKey);
        Assert.Equal(plugin.PluginKey, plugin.Provider);
        Assert.Equal(PluginConstants.PluginKind.Mcp, plugin.Kind);
        Assert.Equal(OwnerId, plugin.CreatedBy);
        Assert.Equal(WorkspacePluginConstants.Availability.Private, result.Value!.Availability);

        var here = await new WorkspacePluginGuard(_unitOfWork, _policyClient, _membershipClient).GetAvailabilityAsync(WorkspaceId);
        var elsewhere = await new WorkspacePluginGuard(_unitOfWork, _policyClient, _membershipClient).GetAvailabilityAsync(OtherWorkspaceId);
        Assert.True(here.IsUsable(plugin));
        Assert.False(elsewhere.IsUsable(plugin));
    }

    [Theory]
    [InlineData("http://crm.example.com/mcp")]
    [InlineData("https://localhost/mcp")]
    [InlineData("https://127.0.0.1/mcp")]
    [InlineData("https://10.0.0.5/mcp")]
    [InlineData("https://192.168.1.10/mcp")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://[::1]/mcp")]
    [InlineData("https://workspace-service/mcp")]
    [InlineData("https://redis.internal/mcp")]
    [InlineData("not a url")]
    public async Task APrivatePluginUrl_MustBeAPublicHttpsAddress(string url)
    {
        var result = await Sut().CreatePrivatePluginAsync(WorkspaceId, OwnerId, new CreatePrivatePluginRequest("CRM", url));

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin, result.ErrorCode);
        Assert.DoesNotContain(_plugins, p => p.OwnerWorkspaceId is not null);
    }

    [Fact]
    public async Task AnOwner_CannotEditAnotherWorkspacesPrivatePlugin()
    {
        var foreign = WorkspacePluginGuardTests.Private("ws_crm_00000000", OtherWorkspaceId);
        _plugins.Add(foreign);

        var result = await Sut().UpdatePrivatePluginAsync(WorkspaceId, OwnerId, foreign.PluginKey, new UpdatePrivatePluginRequest(Label: "Mine now"));

        Assert.Equal(WorkspacePluginConstants.ErrorCodes.NotAPrivatePlugin, result.ErrorCode);
        Assert.Equal("ws_crm_00000000", foreign.Label);
    }

    [Fact]
    public async Task RemovingAPrivatePlugin_RetiresIt()
    {
        var own = WorkspacePluginGuardTests.Private("ws_crm_00000000", WorkspaceId);
        _plugins.Add(own);

        var result = await Sut().RemovePluginAsync(WorkspaceId, OwnerId, own.PluginKey);

        Assert.True(result.IsSuccess);
        Assert.False(own.IsActive);
        Assert.Contains(own, _plugins);
    }

    // ---- helpers the rest of the marketplace relies on -----------------------------------------

    [Theory]
    [InlineData("Internal CRM", "ws_internal_crm_")]
    [InlineData("Quản lý Đơn hàng", "ws_quan_ly_don_hang_")]
    [InlineData("!!!", "ws_plugin_")]
    public void DerivedKeys_AreAsciiPrefixedAndSuffixed(string label, string expectedPrefix)
    {
        var key = McpPluginRows.DerivePrivateKey(label);

        Assert.StartsWith(expectedPrefix, key);
        Assert.Matches("^ws_[a-z0-9_]+_[0-9a-f]{8}$", key);
        Assert.False(PluginConstants.IsReservedPluginKey(key));
    }

    [Theory]
    [InlineData(PluginConstants.ErrorCodes.PermissionDenied, 403)]
    [InlineData(PluginConstants.ErrorCodes.UnknownPlugin, 404)]
    [InlineData(WorkspacePluginConstants.ErrorCodes.UnknownRequest, 404)]
    [InlineData(WorkspacePluginConstants.ErrorCodes.RequestAlreadyPending, 409)]
    [InlineData(WorkspacePluginConstants.ErrorCodes.PluginRetired, 409)]
    [InlineData(WorkspacePluginConstants.ErrorCodes.InvalidPrivatePlugin, 400)]
    [InlineData(WorkspacePluginConstants.ErrorCodes.PolicyUnavailable, 503)]
    [InlineData(WorkspacePluginConstants.ErrorCodes.ListChangedConcurrently, 409)]
    public void TheControllerMapsRefusalsToStatuses(string errorCode, int status)
    {
        Assert.Equal(status, WorkspacePluginsController.StatusFor(errorCode));
    }

    [Fact]
    public void TheControllerRequiresAuthentication_AndLivesUnderTheGatewaysAssistantPrefix()
    {
        var type = typeof(WorkspacePluginsController);
        Assert.NotNull(type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).SingleOrDefault());
        var route = (Microsoft.AspNetCore.Mvc.RouteAttribute)type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), true).Single();
        Assert.StartsWith("api/v1/assistant/", route.Template);
        Assert.DoesNotContain(type.GetMethods(), m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Length > 0);
    }

    // ---- plumbing ------------------------------------------------------------------------------

    private void AllowAnyPlugins(bool allow)
    {
        _policyClient.AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(allow);
        _policyClient.ReadAllowAnyPluginsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(allow ? WorkspacePluginPolicyAnswer.Allowed : WorkspacePluginPolicyAnswer.NotAllowed);
    }

    /// <summary>The workspace service is down: the guard hears "no", the transition hears "unknown".</summary>
    private void PolicyUnreadable()
    {
        _policyClient.AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _policyClient.ReadAllowAnyPluginsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(WorkspacePluginPolicyAnswer.Unknown);
    }

    /// <summary>Every save is refused by a unique key, as a concurrent writer's commit would cause.</summary>
    private void SavesLoseARace() =>
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new DbUpdateException("duplicate key", new FakeDbException("23505")));

    private sealed class FakeDbException(string sqlState) : DbException("test")
    {
        public override string? SqlState { get; } = sqlState;
    }

    private void AlreadyCurated() =>
        _curations.Add(new WorkspacePluginCuration { WorkspaceId = WorkspaceId, CuratedAt = DateTime.UtcNow, CuratedBy = OwnerId });

    private static TRepository InMemory<TRepository, T>(List<T> store, Func<T, Guid> id)
        where TRepository : class, IGenericRepository<T>
        where T : class
    {
        var repository = Substitute.For<TRepository>();
        repository.FindAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<T>)store.Where(call.Arg<Expression<Func<T, bool>>>().Compile()).ToList());
        repository.FirstOrDefaultAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => store.FirstOrDefault(call.Arg<Expression<Func<T, bool>>>().Compile()));
        repository.AnyAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => store.Any(call.Arg<Expression<Func<T, bool>>>().Compile()));
        repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => store.FirstOrDefault(item => id(item) == call.Arg<Guid>()));
        repository.AddAsync(Arg.Any<T>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                store.Add(call.Arg<T>());
                return Task.CompletedTask;
            });
        repository.When(r => r.Remove(Arg.Any<T>())).Do(call => store.Remove(call.Arg<T>()));
        return repository;
    }
}
