using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

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
    /// membership client reports the caller as <paramref name="isActiveMember"/>, in
    /// <paramref name="roleName"/>.
    /// </summary>
    public static WorkspacePluginGuard Guard(bool allowsPluginUsage, bool isActiveMember, string roleName = "Member")
    {
        var policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
        policyClient.AllowsPluginUsageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(allowsPluginUsage);
        return Guard(policyClient, isActiveMember, roleName);
    }

    /// <summary>
    /// Membership defaults to an active member, so a test that is about the policy does not have to
    /// say so. The membership check is the caller's standing, not the workspace's rule, and a test
    /// that stubbed it as "not a member" by omission would pass for the wrong reason.
    /// </summary>
    public static WorkspacePluginGuard Guard(
        IWorkspacePluginPolicyClient policyClient,
        bool isActiveMember = true,
        string roleName = "Member")
    {
        var membershipClient = Substitute.For<IWorkspaceMembershipClient>();
        membershipClient.GetMembershipAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(isActiveMember
                ? new WorkspaceMembership(IsMember: true, RoleName: roleName, IsActive: true)
                : WorkspaceMembership.None);

        // A workspace that has never curated its plugin list and whose members have used every
        // plugin in it: judged by the policy client's AllowAnyPlugins answer alone, which is what
        // the services under test here care about. Which plugins an uncurated workspace carries
        // over is WorkspacePluginGuardTests' business.
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var curations = Substitute.For<IWorkspacePluginCurationRepository>();
        curations.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WorkspacePluginCuration?)null);
        unitOfWork.WorkspacePluginCurationRepository.Returns(curations);
        var audits = Substitute.For<IPluginToolAuditRepository>();
        audits.GetPluginIdsUsedInWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(EveryPlugin.Instance);
        unitOfWork.PluginToolAuditRepository.Returns(audits);

        return new WorkspacePluginGuard(unitOfWork, policyClient, membershipClient);
    }

    /// <summary>"Members have used every plugin here" - a set that contains any id asked about.</summary>
    private sealed class EveryPlugin : IReadOnlySet<Guid>
    {
        public static readonly EveryPlugin Instance = new();

        public int Count => int.MaxValue;
        public bool Contains(Guid item) => true;
        public IEnumerator<Guid> GetEnumerator() => Enumerable.Empty<Guid>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<Guid> other) => false;
        public bool IsProperSupersetOf(IEnumerable<Guid> other) => true;
        public bool IsSubsetOf(IEnumerable<Guid> other) => false;
        public bool IsSupersetOf(IEnumerable<Guid> other) => true;
        public bool Overlaps(IEnumerable<Guid> other) => other.Any();
        public bool SetEquals(IEnumerable<Guid> other) => false;
    }
}
