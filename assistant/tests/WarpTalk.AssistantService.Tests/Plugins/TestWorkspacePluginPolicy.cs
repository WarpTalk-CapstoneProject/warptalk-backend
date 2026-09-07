using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// Builds the guard that applies a workspace's plugin policy, for tests that need a real policy
/// decision rather than a stubbed verdict. WT-646.
/// </summary>
/// <remarks>
/// These helpers hand back the REAL <see cref="WorkspacePluginGuard"/> over a substituted policy
/// client, deliberately. Substituting <see cref="IWorkspacePluginGuard"/> itself would let a test
/// assert that a service refuses when the guard says refuse, which is the uninteresting half; what
/// has to hold is that a workspace's answer produces the right verdict all the way through to the
/// service's own.
/// </remarks>
internal static class TestWorkspacePluginPolicy
{
    /// <summary>
    /// A guard whose policy client gives <paramref name="allowsPluginUsage"/> for every workspace.
    /// </summary>
    public static WorkspacePluginGuard Guard(bool allowsPluginUsage)
    {
        var policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
        policyClient.AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(allowsPluginUsage);
        return Guard(policyClient);
    }

    public static WorkspacePluginGuard Guard(IWorkspacePluginPolicyClient policyClient) =>
        new(policyClient);
}
