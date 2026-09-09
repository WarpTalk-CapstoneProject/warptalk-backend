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

    /// <summary>
    /// A guard whose policy client answers <paramref name="allowsPluginUsage"/> and whose
    /// membership client reports the caller as <paramref name="isActiveMember"/>.
    /// </summary>
    public static WorkspacePluginGuard Guard(bool allowsPluginUsage, bool isActiveMember)
    {
        var policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
        policyClient.AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(allowsPluginUsage);
        return Guard(policyClient, isActiveMember);
    }

    /// <summary>
    /// Membership defaults to an active member, so a test that is about the policy does not have to
    /// say so. The membership check is the caller's standing, not the workspace's rule, and a test
    /// that stubbed it as "not a member" by omission would pass for the wrong reason.
    /// </summary>
    public static WorkspacePluginGuard Guard(
        IWorkspacePluginPolicyClient policyClient,
        bool isActiveMember = true)
    {
        var membershipClient = Substitute.For<IWorkspaceMembershipClient>();
        membershipClient.GetMembershipAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(isActiveMember
                ? new WorkspaceMembership(IsMember: true, RoleName: "Member", IsActive: true)
                : WorkspaceMembership.None);

        return new WorkspacePluginGuard(policyClient, membershipClient);
    }
}
