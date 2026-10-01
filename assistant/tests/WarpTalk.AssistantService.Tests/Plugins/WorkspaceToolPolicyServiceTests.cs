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

/// <summary>
/// The workspace Owner's per-tool rules: who may read and write them, and what may be written.
/// Enforcement is covered by McpToolOrchestratorTests.
/// </summary>
public class WorkspaceToolPolicyServiceTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CallerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PluginId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginRepository _pluginRepository = Substitute.For<IPluginRepository>();
    private readonly IWorkspacePluginToolPolicyRepository _ruleRepository = Substitute.For<IWorkspacePluginToolPolicyRepository>();
    private readonly IWorkspaceMembershipClient _membership = Substitute.For<IWorkspaceMembershipClient>();
    private readonly List<WorkspacePluginToolPolicy> _rules = [];
    private Plugin? _plugin = DrivePlugin();

    public WorkspaceToolPolicyServiceTests()
    {
        _unitOfWork.PluginRepository.Returns(_pluginRepository);
        _unitOfWork.WorkspacePluginToolPolicyRepository.Returns(_ruleRepository);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);
        _pluginRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<Plugin, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => _plugin);
        _ruleRepository.FindAsync(
                Arg.Any<Expression<Func<WorkspacePluginToolPolicy, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<WorkspacePluginToolPolicy>)_rules
                .Where(call.Arg<Expression<Func<WorkspacePluginToolPolicy, bool>>>().Compile())
                .ToList());
        _ruleRepository.FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspacePluginToolPolicy, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call => _rules.FirstOrDefault(
                call.Arg<Expression<Func<WorkspacePluginToolPolicy, bool>>>().Compile()));
        _ruleRepository.AddAsync(Arg.Any<WorkspacePluginToolPolicy>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _rules.Add(call.Arg<WorkspacePluginToolPolicy>());
                return Task.CompletedTask;
            });
        _ruleRepository.When(r => r.Remove(Arg.Any<WorkspacePluginToolPolicy>()))
            .Do(call => _rules.Remove(call.Arg<WorkspacePluginToolPolicy>()));
        Role(WorkspaceRoleConstants.Owner);
    }

    private void Role(string role) =>
        _membership.GetMembershipAsync(WorkspaceId, CallerId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(true, role, true));

    private WorkspaceToolPolicyService CreateSut() => new(_unitOfWork, _membership);

    private static UpdateWorkspaceToolPolicyRequest Set(string tool, string? policy) => new(tool, policy);

    [Fact]
    public async Task SetAsync_StoresARule_AndReadsItBack()
    {
        var result = await CreateSut().SetAsync(
            WorkspaceId, CallerId, "google_drive", Set("google_drive_search", PluginConstants.ToolPolicy.Blocked));

        Assert.True(result.IsSuccess);
        var stored = Assert.Single(_rules);
        Assert.Equal(PluginConstants.ToolPolicy.Blocked, stored.Policy);
        Assert.Equal(CallerId, stored.SetBy);
        var tool = Assert.Single(result.Value!.Tools);
        Assert.Equal(PluginConstants.ToolPolicy.Blocked, tool.WorkspacePolicy);
        Assert.True(result.Value.CanManage);
    }

    [Fact]
    public async Task SetAsync_WithNull_ReturnsTheToolToEachMember()
    {
        var sut = CreateSut();
        await sut.SetAsync(WorkspaceId, CallerId, "google_drive", Set("google_drive_search", PluginConstants.ToolPolicy.Approval));

        var result = await sut.SetAsync(WorkspaceId, CallerId, "google_drive", Set("google_drive_search", null));

        Assert.True(result.IsSuccess);
        Assert.Empty(_rules);
        Assert.Null(Assert.Single(result.Value!.Tools).WorkspacePolicy);
    }

    [Fact]
    public async Task SetAsync_ChangesAnExistingRuleInPlace()
    {
        var sut = CreateSut();
        await sut.SetAsync(WorkspaceId, CallerId, "google_drive", Set("google_drive_search", PluginConstants.ToolPolicy.Approval));

        await sut.SetAsync(WorkspaceId, CallerId, "google_drive", Set("google_drive_search", PluginConstants.ToolPolicy.Blocked));

        Assert.Equal(PluginConstants.ToolPolicy.Blocked, Assert.Single(_rules).Policy);
    }

    [Fact]
    public async Task SetAsync_RefusesAllow_BecauseAWorkspaceRuleOnlyTightens()
    {
        var result = await CreateSut().SetAsync(
            WorkspaceId, CallerId, "google_drive", Set("google_drive_search", PluginConstants.ToolPolicy.Allow));

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.InvalidToolPolicy, result.ErrorCode);
        Assert.Empty(_rules);
    }

    [Fact]
    public async Task SetAsync_RefusesAToolThePluginDoesNotDeclare()
    {
        var result = await CreateSut().SetAsync(
            WorkspaceId, CallerId, "google_drive", Set("delete_everything", PluginConstants.ToolPolicy.Blocked));

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.InvalidToolPolicy, result.ErrorCode);
        Assert.Empty(_rules);
    }

    [Fact]
    public async Task SetAsync_IsTheOwnersAlone()
    {
        Role(WorkspaceRoleConstants.Admin);

        var result = await CreateSut().SetAsync(
            WorkspaceId, CallerId, "google_drive", Set("google_drive_search", PluginConstants.ToolPolicy.Blocked));

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.Empty(_rules);
    }

    [Fact]
    public async Task GetAsync_LetsAnAdminRead_WithoutManaging()
    {
        Role(WorkspaceRoleConstants.Admin);

        var result = await CreateSut().GetAsync(WorkspaceId, CallerId, "google_drive");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.CanManage);
    }

    [Fact]
    public async Task GetAsync_RefusesAMember()
    {
        Role("Member");

        var result = await CreateSut().GetAsync(WorkspaceId, CallerId, "google_drive");

        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
    }

    [Fact]
    public async Task GetAsync_TreatsAnotherWorkspacesPrivatePluginAsUnknown()
    {
        _plugin = DrivePlugin(ownerWorkspaceId: Guid.NewGuid());

        var result = await CreateSut().GetAsync(WorkspaceId, CallerId, "google_drive");

        Assert.Equal(PluginConstants.ErrorCodes.UnknownPlugin, result.ErrorCode);
    }

    private static Plugin DrivePlugin(Guid? ownerWorkspaceId = null) => new()
    {
        Id = PluginId,
        PluginKey = "google_drive",
        Provider = PluginConstants.Providers.Google,
        Label = "Google Drive",
        Description = "Files in Google Drive.",
        IsActive = true,
        OwnerWorkspaceId = ownerWorkspaceId,
        RequiredScopesJson = """["https://www.googleapis.com/auth/drive.readonly"]""",
        ToolsJson = """
            [
              {
                "name": "google_drive_search",
                "pluginKey": "google_drive",
                "label": "Search Google Drive",
                "description": "Search files in Google Drive.",
                "effect": "read",
                "requiredScopes": ["https://www.googleapis.com/auth/drive.readonly"],
                "parameters": { "type": "object", "properties": { "query": { "type": "string" } } }
              }
            ]
            """,
    };
}
