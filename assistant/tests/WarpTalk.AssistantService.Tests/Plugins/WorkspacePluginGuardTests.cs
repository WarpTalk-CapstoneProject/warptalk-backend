using System.Linq.Expressions;
using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The workspace plugin rule: usable iff the workspace added it, or it is the workspace's own private
/// plugin - and the transition that keeps an uncurated workspace on AllowAnyPlugins.
/// </summary>
public class WorkspacePluginGuardTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherWorkspaceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWorkspacePluginCurationRepository _curations = Substitute.For<IWorkspacePluginCurationRepository>();
    private readonly IWorkspacePluginRepository _workspacePlugins = Substitute.For<IWorkspacePluginRepository>();
    private readonly IWorkspacePluginPolicyClient _policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
    private readonly IWorkspaceMembershipClient _membershipClient = Substitute.For<IWorkspaceMembershipClient>();

    private readonly Plugin _linear = Marketplace("linear");
    private readonly Plugin _notion = Marketplace("notion");

    public WorkspacePluginGuardTests()
    {
        _unitOfWork.WorkspacePluginCurationRepository.Returns(_curations);
        _unitOfWork.WorkspacePluginRepository.Returns(_workspacePlugins);
        _curations.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((WorkspacePluginCuration?)null);
        Member(true);
    }

    private WorkspacePluginGuard Guard() => new(_unitOfWork, _policyClient, _membershipClient);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUncuratedWorkspace_IsStillJudgedByAllowAnyPlugins(bool allowAnyPlugins)
    {
        // The transition: nobody loses a plugin on deploy, and a workspace that had switched plugins
        // off does not gain any either.
        _policyClient.AllowsPluginUsageAsync(WorkspaceId, Arg.Any<CancellationToken>()).Returns(allowAnyPlugins);

        var result = await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, UserId, _linear);

        Assert.Equal(allowAnyPlugins, result.IsSuccess);
    }

    [Fact]
    public async Task ACuratedWorkspace_IsJudgedByItsListAlone_WhateverAllowAnyPluginsSays()
    {
        Curated(_notion);
        _policyClient.AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        Assert.True((await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, UserId, _notion)).IsSuccess);

        var linear = await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, UserId, _linear);
        Assert.False(linear.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, linear.ErrorCode);
        Assert.Equal(WorkspacePluginConstants.Messages.NotAdded, linear.Error);

        // Once curated, the old switch is not even asked.
        await _policyClient.DidNotReceive().AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACuratedWorkspaceWithAnEmptyList_HasNoPlugins()
    {
        // Why the curation row exists: removing every plugin must not fall back to "every plugin".
        Curated();

        Assert.False((await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, UserId, _linear)).IsSuccess);
    }

    [Fact]
    public async Task APrivatePlugin_IsUsableInItsOwnWorkspace_WithoutBeingOnTheList()
    {
        Curated();
        var crm = Private("ws_crm_0000", WorkspaceId);

        Assert.True((await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, UserId, crm)).IsSuccess);
    }

    [Fact]
    public async Task APrivatePlugin_IsInvisibleToEveryOtherWorkspace_EvenOneThatAllowsEverything()
    {
        _policyClient.AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var crm = Private("ws_crm_0000", OtherWorkspaceId);

        var inWorkspace = await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, UserId, crm);
        var personal = await Guard().CanUsePluginAsync(WorkspaceId, UserId, crm);

        Assert.False(inWorkspace.IsSuccess);
        Assert.False(personal.IsSuccess);
    }

    [Fact]
    public async Task APrivatePlugin_WithNoWorkspaceNamed_NeedsMembershipOfItsOwnWorkspace()
    {
        var crm = Private("ws_crm_0000", OtherWorkspaceId);
        _membershipClient.GetMembershipAsync(OtherWorkspaceId, UserId, Arg.Any<CancellationToken>())
            .Returns(WorkspaceMembership.None);

        Assert.False((await Guard().CanUsePluginAsync(null, UserId, crm)).IsSuccess);

        _membershipClient.GetMembershipAsync(OtherWorkspaceId, UserId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(true, "Member", true));

        Assert.True((await Guard().CanUsePluginAsync(null, UserId, crm)).IsSuccess);
    }

    [Fact]
    public async Task NoWorkspace_PermitsAnyMarketplacePlugin_WithoutAskingAnything()
    {
        // The personal plugins page: installing and connecting are personal.
        Assert.True((await Guard().CanUsePluginAsync(null, UserId, _linear)).IsSuccess);

        await _policyClient.DidNotReceive().AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _curations.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InsideAWorkspace_ANonMemberIsRefused_BeforeTheListIsRead()
    {
        Member(false);
        Curated(_linear);

        var result = await Guard().CanUsePluginInWorkspaceAsync(WorkspaceId, UserId, _linear);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.WorkspacePolicyMessages.NotAWorkspaceMember, result.Error);
        await _curations.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InsideAWorkspace_AMissingWorkspaceIsARefusal()
    {
        var result = await Guard().CanUsePluginInWorkspaceAsync(null, UserId, _linear);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.WorkspacePolicyMessages.WorkspaceRequired, result.Error);
    }

    [Fact]
    public async Task AnUnreachableWorkspaceServiceDenies()
    {
        // The policy client answers false when it cannot reach the workspace service; an outage must
        // not lift the restriction on an uncurated workspace.
        _policyClient.AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.False((await Guard().CanUsePluginAsync(WorkspaceId, UserId, _linear)).IsSuccess);
    }

    private void Member(bool isMember) =>
        _membershipClient.GetMembershipAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(isMember ? new WorkspaceMembership(true, "Member", true) : WorkspaceMembership.None);

    private void Curated(params Plugin[] added)
    {
        _curations.GetByIdAsync(WorkspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspacePluginCuration { WorkspaceId = WorkspaceId, CuratedAt = DateTime.UtcNow });
        _workspacePlugins.FindAsync(Arg.Any<Expression<Func<WorkspacePlugin, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(added.Select(p => new WorkspacePlugin { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, PluginId = p.Id }).ToList());
    }

    internal static Plugin Marketplace(string key) => new()
    {
        Id = Guid.NewGuid(),
        PluginKey = key,
        Label = key,
        Description = key,
        Provider = key,
        RequiredScopesJson = "[]",
        ToolsJson = "[]",
        Kind = PluginConstants.PluginKind.Mcp,
        McpServerUrl = $"https://{key}.example.com/mcp",
        OAuthClientSource = PluginConstants.OAuthClientSource.Unresolved,
        IsActive = true,
    };

    internal static Plugin Private(string key, Guid ownerWorkspaceId)
    {
        var plugin = Marketplace(key);
        plugin.OwnerWorkspaceId = ownerWorkspaceId;
        return plugin;
    }
}
