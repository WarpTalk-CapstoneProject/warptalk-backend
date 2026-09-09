using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The workspace plugin policy semantics themselves. WT-646.
/// </summary>
public class WorkspacePluginGuardTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheWorkspacesAnswerIsTheWholePolicy(bool allowsPluginUsage)
    {
        // A workspace owner configures exactly one thing about plugins, and this is it.
        var guard = TestWorkspacePluginPolicy.Guard(allowsPluginUsage);

        var result = await guard.CanUsePluginsAsync(WorkspaceId);

        Assert.Equal(allowsPluginUsage, result.IsSuccess);
        if (!allowsPluginUsage)
        {
            Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
            Assert.Equal(PluginConstants.WorkspacePolicyMessages.PluginsDisabled, result.Error);
        }
    }

    [Fact]
    public async Task NoWorkspace_PermitsEverythingWithoutAskingTheWorkspaceService()
    {
        // A user outside any workspace context - the personal plugins page - has no workspace
        // policy to answer to, and denying them would take away access that works today. The
        // round trip is skipped as well as the verdict.
        var policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
        var guard = TestWorkspacePluginPolicy.Guard(policyClient);

        Assert.True((await guard.CanUsePluginsAsync(null)).IsSuccess);

        await policyClient.DidNotReceive()
            .AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnreachableWorkspaceServiceDenies()
    {
        // The gRPC client answers false when the workspace service is unreachable or does not know
        // the workspace, and the guard must pass that through as a refusal. Failing open here
        // would turn any workspace-service outage into a product-wide lifting of the restriction.
        var guard = TestWorkspacePluginPolicy.Guard(allowsPluginUsage: false);

        var result = await guard.CanUsePluginsAsync(WorkspaceId);

        Assert.False(result.IsSuccess);
    }
}
