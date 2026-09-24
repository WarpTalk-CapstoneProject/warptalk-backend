using NSubstitute;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The platform admin's per-workspace plugin controls: the default, overrides one at a time and in
/// bulk by plan, both views of the result, and an audit record before every commit.
/// </summary>
public class PluginWorkspaceAccessAdminServiceTests
{
    private static readonly Guid AdminId = Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");

    private readonly List<Plugin> _plugins = [];
    private readonly PluginWorkspaceFixture _world = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAdminAuditRecorder _audit = Substitute.For<IAdminAuditRecorder>();
    private readonly List<(string Action, Guid WorkspaceId, string? Reason, int SavesSoFar)> _workspaceRecords = [];
    private readonly List<(string Action, int SavesSoFar)> _pluginRecords = [];
    private Result _auditAnswer = Result.Success();
    private int _saves;

    private readonly Plugin _linear;

    public PluginWorkspaceAccessAdminServiceTests()
    {
        var plugins = InMemoryRepository.Create<IPluginRepository, Plugin>(_plugins, p => p.Id);
        _unitOfWork.PluginRepository.Returns(plugins);
        var listRows = Substitute.For<IWorkspacePluginRepository>();
        var audits = Substitute.For<IPluginToolAuditRepository>();
        var installations = Substitute.For<IPluginInstallationRepository>();
        var connections = Substitute.For<IPluginConnectionRepository>();
        _world.Wire(_unitOfWork, listRows, audits, installations, connections);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ => ++_saves);

        _audit.RecordPluginWorkspaceActionAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<IReadOnlyDictionary<string, string?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _workspaceRecords.Add((call.ArgAt<string>(0), call.ArgAt<Guid>(2), call.ArgAt<string?>(4), _saves));
                return _auditAnswer;
            });
        _audit.RecordPluginActionAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<IReadOnlyDictionary<string, string?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _pluginRecords.Add((call.ArgAt<string>(0), _saves));
                return _auditAnswer;
            });

        _linear = WorkspacePluginGuardTests.Marketplace("linear");
        _plugins.Add(_linear);
    }

    private PluginWorkspaceAccessAdminService Sut() => new(_unitOfWork, _world.Directory, _audit);

    // ---- the plugin's "Workspaces" tab ----------------------------------------------------------

    [Fact]
    public async Task GetForPlugin_ListsEveryWorkspace_InheritedOrOverridden_WithWhoConnectedAndUsage()
    {
        var free = _world.AddWorkspace("Free Co", plan: "free");
        var biz = _world.AddWorkspace("Biz Co", plan: "business");
        var none = _world.AddWorkspace("New Co");
        var member = _world.Connect(_linear, biz);
        _world.Override(_linear, free, PluginWorkspaceAccessConstants.OverrideState.Disabled, "trial abuse");
        _world.Usage.Add(new WorkspacePluginUsage(biz.WorkspaceId, _linear.Id, 12, DateTime.UtcNow));

        var result = await Sut().GetForPluginAsync("linear");

        Assert.True(result.IsSuccess);
        var rows = result.Value!.Workspaces.ToDictionary(r => r.WorkspaceId);
        Assert.Equal(3, rows.Count);

        Assert.False(rows[free.WorkspaceId].Enabled);
        Assert.Equal("override", rows[free.WorkspaceId].Source);
        Assert.Equal("trial abuse", rows[free.WorkspaceId].OverrideReason);

        var bizRow = rows[biz.WorkspaceId];
        Assert.True(bizRow.Enabled);
        Assert.Equal("default", bizRow.Source);
        Assert.Equal([member], bizRow.ConnectedUserIds);
        Assert.Equal(12, bizRow.UsageCount);
        Assert.True(bizRow.InUse);

        Assert.True(rows[none.WorkspaceId].Enabled);
        Assert.False(rows[none.WorkspaceId].InUse);
    }

    [Fact]
    public async Task GetForPlugin_Is503_NotEmpty_WhenTheWorkspaceServiceIsDown()
    {
        _world.DirectoryDown = true;

        var result = await Sut().GetForPluginAsync("linear");

        Assert.Equal(PluginWorkspaceAccessConstants.ErrorCodes.WorkspacesUnavailable, result.ErrorCode);
        Assert.Equal(503, AdminPluginWorkspaceAccessController.StatusFor(result.ErrorCode));
    }

    [Fact]
    public async Task APrivatePlugin_IsNotReachableHere()
    {
        _plugins.Add(WorkspacePluginGuardTests.Private("ws_crm_1", Guid.NewGuid()));

        var result = await Sut().GetForPluginAsync("ws_crm_1");

        Assert.Equal(PluginConstants.ErrorCodes.UnknownPlugin, result.ErrorCode);
    }

    // ---- bulk overrides -------------------------------------------------------------------------

    [Fact]
    public async Task ApplyOverride_ByPlan_ChangesOnlyWorkspacesOnThosePlans_AndAuditsEachBeforeTheCommit()
    {
        var free = _world.AddWorkspace("Free Co", plan: "free");
        var biz = _world.AddWorkspace("Biz Co", plan: "business");
        var ent = _world.AddWorkspace("Ent Co", plan: "Enterprise");

        var result = await Sut().ApplyOverrideAsync(
            "linear",
            new ApplyPluginOverrideRequest("enable", "pilot", PlanSlugs: ["business", "enterprise"]),
            AdminId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Changed);
        Assert.Equal(new[] { biz.WorkspaceId, ent.WorkspaceId }.Order(), _world.Overrides.Select(o => o.WorkspaceId).Order());
        Assert.DoesNotContain(_world.Overrides, o => o.WorkspaceId == free.WorkspaceId);
        Assert.All(_world.Overrides, o =>
        {
            Assert.Equal(PluginWorkspaceAccessConstants.OverrideState.Enabled, o.State);
            Assert.Equal("pilot", o.Reason);
            Assert.Equal(AdminId, o.SetBy);
        });

        Assert.Equal(2, _workspaceRecords.Count);
        Assert.All(_workspaceRecords, r =>
        {
            Assert.Equal(AdminAuditPluginActions.WorkspaceEnabled, r.Action);
            Assert.Equal("pilot", r.Reason);
            Assert.Equal(0, r.SavesSoFar);
        });
        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task ApplyOverride_WithIdsAndAPlan_ChangesOnlyTheChosenWorkspacesOnThosePlans()
    {
        var free = _world.AddWorkspace("Free Co", plan: "free");
        var biz = _world.AddWorkspace("Biz Co", plan: "business");
        _world.AddWorkspace("Other Biz", plan: "business");

        var result = await Sut().ApplyOverrideAsync(
            "linear",
            new ApplyPluginOverrideRequest("disable", "vendor outage", [free.WorkspaceId, biz.WorkspaceId], ["business"]),
            AdminId);

        Assert.Equal(1, result.Value!.Changed);
        var row = Assert.Single(_world.Overrides);
        Assert.Equal(biz.WorkspaceId, row.WorkspaceId);
    }

    [Fact]
    public async Task ApplyOverride_CountsWorkspacesAlreadyInThatState_AsUnchanged_AndRecordsNothingForThem()
    {
        var a = _world.AddWorkspace("A");
        var b = _world.AddWorkspace("B");
        _world.Override(_linear, a, PluginWorkspaceAccessConstants.OverrideState.Disabled, "security review");

        var result = await Sut().ApplyOverrideAsync(
            "linear",
            new ApplyPluginOverrideRequest("disable", "security review", [a.WorkspaceId, b.WorkspaceId]),
            AdminId);

        Assert.Equal(1, result.Value!.Changed);
        Assert.Equal(1, result.Value.Unchanged);
        Assert.Equal(b.WorkspaceId, Assert.Single(_workspaceRecords).WorkspaceId);
    }

    [Fact]
    public async Task Disabling_WithoutAReason_IsRefused_AndNothingIsWritten()
    {
        var a = _world.AddWorkspace("A");

        var result = await Sut().ApplyOverrideAsync(
            "linear", new ApplyPluginOverrideRequest("disable", "  ", [a.WorkspaceId]), AdminId);

        Assert.Equal(PluginWorkspaceAccessConstants.ErrorCodes.InvalidOverride, result.ErrorCode);
        Assert.Empty(_world.Overrides);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task AnIdThatNamesNoWorkspace_IsRefused_AndNothingIsWritten()
    {
        var a = _world.AddWorkspace("A");

        var result = await Sut().ApplyOverrideAsync(
            "linear", new ApplyPluginOverrideRequest("enable", null, [a.WorkspaceId, Guid.NewGuid()]), AdminId);

        Assert.Equal(PluginWorkspaceAccessConstants.ErrorCodes.UnknownWorkspace, result.ErrorCode);
        Assert.Equal(404, AdminPluginWorkspaceAccessController.StatusFor(result.ErrorCode));
        Assert.Empty(_world.Overrides);
    }

    [Fact]
    public async Task Reset_RemovesTheOverride_SoTheWorkspaceFollowsTheDefaultAgain()
    {
        var a = _world.AddWorkspace("A");
        _world.Override(_linear, a, PluginWorkspaceAccessConstants.OverrideState.Disabled, "security review");

        var result = await Sut().SetWorkspaceOverrideAsync(
            a.WorkspaceId, "linear", new SetWorkspacePluginOverrideRequest("reset"), AdminId);

        Assert.True(result.IsSuccess);
        Assert.Empty(_world.Overrides);
        Assert.True(result.Value!.Enabled);
        Assert.Equal("default", result.Value.Source);
        Assert.Equal(AdminAuditPluginActions.WorkspaceReset, Assert.Single(_workspaceRecords).Action);
    }

    [Fact]
    public async Task WhenTheAuditLogRefuses_NothingIsSaved()
    {
        var a = _world.AddWorkspace("A");
        _auditAnswer = Result.Failure("audit down", ErrorCodes.ServiceUnavailable);

        var result = await Sut().ApplyOverrideAsync(
            "linear", new ApplyPluginOverrideRequest("enable", null, [a.WorkspaceId]), AdminId);

        Assert.Equal(ErrorCodes.ServiceUnavailable, result.ErrorCode);
        Assert.Equal(503, AdminPluginWorkspaceAccessController.StatusFor(result.ErrorCode));
        Assert.Equal(0, _saves);
    }

    // ---- the default ----------------------------------------------------------------------------

    [Fact]
    public async Task SetAvailability_StoresOptInAndThePlanRule_Normalized()
    {
        var result = await Sut().SetAvailabilityAsync(
            "linear", new SetPluginAvailabilityRequest("opt_in", [" Business ", "enterprise", "business"]), AdminId);

        Assert.True(result.IsSuccess);
        Assert.Equal("opt_in", result.Value!.Default);
        Assert.Equal(["business", "enterprise"], result.Value.AllowedPlans);
        Assert.Equal(PluginWorkspaceAccessConstants.Default.OptIn, _linear.WorkspaceDefault);
        Assert.True(_linear.IsActive);
        Assert.Equal(AdminId, _linear.UpdatedBy);
        Assert.Equal((AdminAuditPluginActions.AvailabilitySet, 0), Assert.Single(_pluginRecords));
    }

    [Fact]
    public async Task SetAvailability_Retired_IsTheSameRetirementASoftDeleteWrites_AndAnEmptyPlanListMeansEveryPlan()
    {
        _linear.AllowedPlanSlugsJson = """["business"]""";

        var result = await Sut().SetAvailabilityAsync("linear", new SetPluginAvailabilityRequest("retired", []), AdminId);

        Assert.Equal("retired", result.Value!.Default);
        Assert.False(_linear.IsActive);
        Assert.Null(_linear.AllowedPlanSlugsJson);
        Assert.Equal(AdminAuditPluginActions.Retired, Assert.Single(_pluginRecords).Action);
    }

    [Theory]
    [InlineData("everyone", null)]
    [InlineData("available", "Business Plan!")]
    public async Task SetAvailability_RefusesWhatItCannotStore(string chosen, string? plan)
    {
        var result = await Sut().SetAvailabilityAsync(
            "linear", new SetPluginAvailabilityRequest(chosen, plan is null ? null : [plan]), AdminId);

        Assert.Equal(PluginWorkspaceAccessConstants.ErrorCodes.InvalidAvailability, result.ErrorCode);
        Assert.Equal(PluginWorkspaceAccessConstants.Default.Available, _linear.WorkspaceDefault);
        Assert.Equal(0, _saves);
    }

    // ---- the workspace's "Plugins" tab ----------------------------------------------------------

    [Fact]
    public async Task GetForWorkspace_ListsEveryMarketplacePlugin_WithThePlanRuleApplied()
    {
        var notion = WorkspacePluginGuardTests.Marketplace("notion");
        notion.AllowedPlanSlugsJson = """["enterprise"]""";
        _plugins.Add(notion);
        _plugins.Add(WorkspacePluginGuardTests.Private("ws_other", Guid.NewGuid()));
        var biz = _world.AddWorkspace("Biz Co", plan: "business");
        var member = _world.Connect(_linear, biz);

        var result = await Sut().GetForWorkspaceAsync(biz.WorkspaceId);

        Assert.True(result.IsSuccess);
        Assert.Equal("business", result.Value!.PlanSlug);
        var rows = result.Value.Plugins.ToDictionary(r => r.PluginKey);
        Assert.Equal(["linear", "notion"], rows.Keys.Order());
        Assert.True(rows["linear"].Enabled);
        Assert.Equal([member], rows["linear"].ConnectedUserIds);
        Assert.False(rows["notion"].Enabled);
        Assert.Equal("plan", rows["notion"].Source);
    }

    [Fact]
    public async Task GetForWorkspace_ForAWorkspaceThatDoesNotExist_Is404()
    {
        var result = await Sut().GetForWorkspaceAsync(Guid.NewGuid());

        Assert.Equal(404, AdminPluginWorkspaceAccessController.StatusFor(result.ErrorCode));
    }
}
