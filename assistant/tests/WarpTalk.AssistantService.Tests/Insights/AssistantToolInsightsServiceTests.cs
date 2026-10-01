using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.AssistantService.Tests.Insights;

/// <summary>
/// Wave 4: GET .../workspaces/{id}/insights/tools and GET .../admin/insights/tools.
/// </summary>
public class AssistantToolInsightsServiceTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherWorkspaceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CallerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly List<AssistantToolCallInsightRow> _rows = [];
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAssistantToolCallRepository _repository = Substitute.For<IAssistantToolCallRepository>();
    private readonly IWorkspaceMembershipClient _membershipClient = Substitute.For<IWorkspaceMembershipClient>();

    public AssistantToolInsightsServiceTests()
    {
        _unitOfWork.AssistantToolCallRepository.Returns(_repository);
        // The repository's contract over an in-memory list: [from, to), one workspace or all.
        _repository.ListForInsightsAsync(Arg.Any<Guid?>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<AssistantToolCallInsightRow>)InWindow(
                call.ArgAt<Guid?>(0), call.ArgAt<DateTime>(1), call.ArgAt<DateTime>(2)).ToList());
        _repository.CountForPeriodAsync(Arg.Any<Guid?>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var rows = InWindow(call.ArgAt<Guid?>(0), call.ArgAt<DateTime>(1), call.ArgAt<DateTime>(2)).ToList();
                return (rows.Count, rows.Count(r => r.Outcome == AssistantToolCallConstants.Outcomes.Ok));
            });
        _repository.GetEarliestCreatedAtAsync(Arg.Any<CancellationToken>())
            .Returns(_ => _rows.Count == 0 ? null : _rows.Min(r => r.CreatedAt));
    }

    // ---- Auth -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(WorkspaceRoleConstants.Owner)]
    [InlineData(WorkspaceRoleConstants.Admin)]
    public async Task Workspace_AnOwnerOrAdmin_IsAnswered(string role)
    {
        Caller(role);

        var result = await Sut().GetWorkspaceAsync(WorkspaceId, CallerId, null, null);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Workspace_AnOrdinaryMember_IsRefused_BeforeAnythingIsRead()
    {
        Caller("Member");

        var result = await Sut().GetWorkspaceAsync(WorkspaceId, CallerId, null, null);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        await _repository.DidNotReceiveWithAnyArgs().ListForInsightsAsync(default, default, default, default);
    }

    [Fact]
    public async Task Workspace_IsRefused_WhenTheWorkspaceServiceCannotAnswer()
    {
        _membershipClient.GetMembershipAsync(WorkspaceId, CallerId, Arg.Any<CancellationToken>())
            .Returns(WorkspaceMembership.None);

        var result = await Sut().GetWorkspaceAsync(WorkspaceId, CallerId, null, null);

        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
    }

    [Fact]
    public async Task WorkspaceController_MapsPermissionDeniedTo403()
    {
        var service = Substitute.For<IAssistantToolInsightsService>();
        service.GetWorkspaceAsync(default, default, default, default, default).ReturnsForAnyArgs(
            Result.Failure<WorkspaceToolInsightsDto>("no", PluginConstants.ErrorCodes.PermissionDenied));
        var controller = new WorkspaceToolInsightsController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var response = await controller.Get(WorkspaceId, null, null, CancellationToken.None);

        Assert.Equal(403, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.NotNull(typeof(WorkspaceToolInsightsController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void AdminEndpoint_RequiresPluginsRead()
    {
        var action = typeof(AdminToolInsightsController).GetMethod(nameof(AdminToolInsightsController.Get))!;

        var permission = Assert.Single(action.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(AdminPermissions.PluginsRead, permission.Permission);
        Assert.Equal(
            "api/v1/assistant/admin/insights/tools",
            typeof(AdminToolInsightsController).GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void WorkspaceEndpoint_Route()
    {
        Assert.Equal(
            "api/v1/assistant/workspaces/{workspaceId:guid}/insights/tools",
            typeof(WorkspaceToolInsightsController).GetCustomAttribute<RouteAttribute>()!.Template);
    }

    // ---- Window ---------------------------------------------------------------------------------

    [Fact]
    public async Task DefaultWindow_IsTheLast30Days()
    {
        Caller(WorkspaceRoleConstants.Owner);

        var result = await Sut().GetWorkspaceAsync(WorkspaceId, CallerId, null, null);

        Assert.Equal(Now.UtcDateTime, result.Value!.To);
        Assert.Equal(Now.UtcDateTime.AddDays(-30), result.Value.From);
        // 2026-09-01T12:00 .. 2026-10-01T12:00 touches 31 UTC days.
        Assert.Equal(31, result.Value.ByDay.Count);
    }

    [Fact]
    public async Task AWindowLongerThan180Days_IsClampedToItsLast180()
    {
        var to = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await Sut().GetPlatformAsync(to.AddDays(-400), to);

        Assert.Equal(to.AddDays(-180), result.Value!.From);
        Assert.Equal(180, result.Value.ByDay.Count);
    }

    [Fact]
    public async Task FromNotBeforeTo_IsAValidationError()
    {
        var at = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await Sut().GetPlatformAsync(at, at);

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    // ---- Buckets --------------------------------------------------------------------------------

    [Fact]
    public async Task ByDay_FillsEveryDay_AndSplitsSourcesAndOkFailed()
    {
        Caller(WorkspaceRoleConstants.Owner);
        var from = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Row("create_meeting", "builtin", null, "ok", 100, from.AddHours(1));
        Row("google_calendar_create_event", "plugin", "google_calendar", "blocked", null, from.AddHours(2));
        Row("google_calendar_create_event", "plugin", "google_calendar", "needs_setup", null, from.AddHours(3));
        Row("web_search", "web_search", null, "error", null, from.AddHours(4));
        Row("google_calendar_create_event", "plugin", "google_calendar", "declined", null, from.AddHours(5));
        Row("google_calendar_create_event", "plugin", "google_calendar", "confirmation_required", null, from.AddHours(6));
        // 2026-09-29: nothing. 2026-09-30: one ok built-in at 23:59.
        Row("create_meeting", "builtin", null, "ok", 300, from.AddDays(2).AddHours(23).AddMinutes(59));
        // Outside the window and in another workspace: neither counts.
        Row("create_meeting", "builtin", null, "ok", 1, to);
        Row("create_meeting", "builtin", null, "ok", 1, from.AddHours(1), OtherWorkspaceId);

        var result = (await Sut().GetWorkspaceAsync(WorkspaceId, CallerId, from, to)).Value!;

        Assert.Collection(
            result.ByDay,
            day => Assert.Equal(new ToolInsightsDayDto("2026-09-28", 1, 4, 1, 1, 3), day),
            day => Assert.Equal(new ToolInsightsDayDto("2026-09-29", 0, 0, 0, 0, 0), day),
            day => Assert.Equal(new ToolInsightsDayDto("2026-09-30", 1, 0, 0, 1, 0), day));

        Assert.Equal(new ToolInsightsTotalsDto(7, 2, 1, 1, 1, 1, 1, 200), result.Totals);
        Assert.Equal(
            [new("builtin", 2), new("plugin", 4), new("web_search", 1)],
            result.BySource);

        var calendar = result.ByTool[0];
        Assert.Equal("google_calendar_create_event", calendar.Tool);
        Assert.Equal("plugin", calendar.Source);
        Assert.Equal("google_calendar", calendar.PluginKey);
        Assert.Equal(4, calendar.Calls);
        Assert.Equal((0, 0, 1, 1), (calendar.Ok, calendar.Error, calendar.Blocked, calendar.NeedsSetup));
        Assert.Null(calendar.MedianDurationMs);
        Assert.Equal(from.AddHours(6), calendar.LastCalledAt);

        var meeting = result.ByTool.Single(t => t.Tool == "create_meeting");
        Assert.Equal(2, meeting.Calls);
        Assert.Equal(200, meeting.MedianDurationMs);
        Assert.Equal(from.AddDays(2).AddHours(23).AddMinutes(59), meeting.LastCalledAt);
    }

    [Fact]
    public async Task PreviousPeriod_IsTheSameLengthImmediatelyBefore()
    {
        Caller(WorkspaceRoleConstants.Owner);
        var from = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Row("a", "builtin", null, "ok", null, from.AddDays(-10));          // first instant of the previous window
        Row("a", "builtin", null, "error", null, from.AddTicks(-1));       // last instant of it
        Row("a", "builtin", null, "ok", null, from.AddDays(-10).AddTicks(-1)); // before it
        Row("a", "builtin", null, "ok", null, from);                       // the current window

        var workspace = (await Sut().GetWorkspaceAsync(WorkspaceId, CallerId, from, to)).Value!;
        var platform = (await Sut().GetPlatformAsync(from, to)).Value!;

        Assert.Equal(2, workspace.PreviousPeriodCalls);
        Assert.Equal(2, platform.PreviousPeriodCalls);
        Assert.Equal(0.5, platform.PreviousSuccessRate);
        Assert.Equal(from.AddDays(-10).AddTicks(-1), workspace.RecordingSince);
    }

    [Fact]
    public async Task Platform_NoPreviousCalls_HasNoPreviousSuccessRate_AndEmptyTableHasNoRecordingSince()
    {
        var result = (await Sut().GetPlatformAsync(null, null)).Value!;

        Assert.Null(result.PreviousSuccessRate);
        Assert.Null(result.RecordingSince);
        Assert.Equal(0, result.Totals.Calls);
        Assert.Null(result.Totals.MedianDurationMs);
        Assert.All(result.BySource, source => Assert.Equal(0, source.Calls));
    }

    [Fact]
    public async Task Platform_CountsWorkspacesPerTool_AndTopWorkspacesByCalls()
    {
        var from = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);
        Row("a", "builtin", null, "ok", 10, from.AddHours(1));
        Row("a", "builtin", null, "error", 20, from.AddHours(2));
        Row("a", "builtin", null, "ok", 30, from.AddHours(3), OtherWorkspaceId);
        Row("b", "builtin", null, "blocked", 40, from.AddHours(4), OtherWorkspaceId);
        Row("b", "builtin", null, "ok", 40, from.AddHours(5), OtherWorkspaceId);

        var result = (await Sut().GetPlatformAsync(from, to)).Value!;

        var a = result.ByTool.Single(t => t.Tool == "a");
        Assert.Equal(2, a.Workspaces);
        Assert.Equal(20, a.MedianDurationMs);
        Assert.Equal(1, result.ByTool.Single(t => t.Tool == "b").Workspaces);
        Assert.Equal(
            [new(OtherWorkspaceId, 3, 1), new(WorkspaceId, 2, 1)],
            result.ByWorkspace);
    }

    [Fact]
    public async Task Platform_ByWorkspace_IsCappedAtTwenty()
    {
        var from = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
            Row("a", "builtin", null, "ok", null, from.AddMinutes(i), Guid.NewGuid());

        var result = (await Sut().GetPlatformAsync(from, from.AddDays(1))).Value!;

        Assert.Equal(AssistantToolInsightsService.TopWorkspaces, result.ByWorkspace.Count);
    }

    [Fact]
    public async Task Json_UsesTheContractPropertyNames()
    {
        Caller(WorkspaceRoleConstants.Owner);
        var from = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        Row("a", "web_search", null, "ok", null, from.AddHours(1));
        var workspace = (await Sut().GetWorkspaceAsync(WorkspaceId, CallerId, from, from.AddDays(1))).Value!;
        var platform = (await Sut().GetPlatformAsync(from, from.AddDays(1))).Value!;

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var w = JsonDocument.Parse(JsonSerializer.Serialize(workspace, web));
        using var p = JsonDocument.Parse(JsonSerializer.Serialize(platform, web));

        Assert.Equal(
            ["from", "to", "recordingSince", "totals", "bySource", "byDay", "byTool", "previousPeriodCalls"],
            w.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(
            ["calls", "ok", "error", "blocked", "needsSetup", "declined", "confirmationRequired", "medianDurationMs"],
            w.RootElement.GetProperty("totals").EnumerateObject().Select(x => x.Name));
        Assert.Equal(
            ["date", "builtin", "plugin", "webSearch", "ok", "failed"],
            w.RootElement.GetProperty("byDay")[0].EnumerateObject().Select(x => x.Name));
        Assert.Equal(
            ["tool", "source", "pluginKey", "calls", "ok", "error", "blocked", "needsSetup", "medianDurationMs", "lastCalledAt"],
            w.RootElement.GetProperty("byTool")[0].EnumerateObject().Select(x => x.Name));
        Assert.Equal(
            ["from", "to", "recordingSince", "totals", "bySource", "byDay", "byTool", "previousPeriodCalls", "byWorkspace", "previousSuccessRate"],
            p.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(
            "workspaces",
            p.RootElement.GetProperty("byTool")[0].EnumerateObject().Last().Name);
        Assert.Equal(
            ["workspaceId", "calls", "failed"],
            p.RootElement.GetProperty("byWorkspace")[0].EnumerateObject().Select(x => x.Name));
        Assert.EndsWith("Z", w.RootElement.GetProperty("from").GetString());
    }

    private IEnumerable<AssistantToolCallInsightRow> InWindow(Guid? workspaceId, DateTime from, DateTime to) =>
        _rows.Where(r => r.CreatedAt >= from && r.CreatedAt < to && (workspaceId == null || r.WorkspaceId == workspaceId));

    private void Row(string tool, string source, string? pluginKey, string outcome, int? durationMs, DateTime at, Guid? workspaceId = null) =>
        _rows.Add(new AssistantToolCallInsightRow(workspaceId ?? WorkspaceId, tool, source, pluginKey, outcome, durationMs, at));

    private void Caller(string role) =>
        _membershipClient.GetMembershipAsync(WorkspaceId, CallerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: true, RoleName: role, IsActive: true));

    private AssistantToolInsightsService Sut() => new(_unitOfWork, _membershipClient, new FixedTimeProvider(Now));
}
