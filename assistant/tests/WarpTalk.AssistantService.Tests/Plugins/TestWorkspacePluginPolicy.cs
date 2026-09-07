using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// Builds workspace plugin policies and the guard that applies them, for tests that need a real
/// policy decision rather than a stubbed verdict. WT-646.
/// </summary>
/// <remarks>
/// These helpers hand back the REAL <see cref="WorkspacePluginGuard"/> over substituted clients,
/// deliberately. Substituting <see cref="IWorkspacePluginGuard"/> itself would let a test assert
/// that a service refuses when the guard says refuse, which is the uninteresting half; what has to
/// hold is that a particular policy - and above all a null allowlist as against an empty one -
/// produces the right verdict all the way through to the service's answer.
/// </remarks>
internal static class TestWorkspacePluginPolicy
{
    /// <summary>
    /// What a workspace service older than WT-646 produces: no <c>plugin_policy</c> message on the
    /// wire, so the client reports no allowlist, member installs permitted and no approval gate,
    /// leaving <c>allow_any_plugins</c> as the whole answer.
    /// </summary>
    public static WorkspacePluginPolicySnapshot LegacyPeer(bool allowAnyPlugins) =>
        new(
            AllowAnyPlugins: allowAnyPlugins,
            AllowedPluginKeys: null,
            AllowMemberPluginInstall: true,
            RequirePluginApproval: false);

    /// <summary>A workspace that configured an allowlist. An empty <paramref name="allowedKeys"/> permits nothing.</summary>
    public static WorkspacePluginPolicySnapshot WithAllowlist(
        bool allowAnyPlugins = true,
        bool allowMemberPluginInstall = true,
        bool requirePluginApproval = false,
        params string[] allowedKeys) =>
        new(
            AllowAnyPlugins: allowAnyPlugins,
            AllowedPluginKeys: allowedKeys.ToList(),
            AllowMemberPluginInstall: allowMemberPluginInstall,
            RequirePluginApproval: requirePluginApproval);

    /// <summary>A workspace with no allowlist that has turned member installs off.</summary>
    public static WorkspacePluginPolicySnapshot MembersMayNotInstall() =>
        new(
            AllowAnyPlugins: true,
            AllowedPluginKeys: null,
            AllowMemberPluginInstall: false,
            RequirePluginApproval: false);

    /// <summary>
    /// A guard whose policy client answers <paramref name="policy"/> for every workspace, and whose
    /// membership client reports <paramref name="roleName"/> for every user.
    /// </summary>
    public static WorkspacePluginGuard Guard(
        WorkspacePluginPolicySnapshot policy,
        string roleName = WorkspaceRoleConstants.Owner)
    {
        var policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
        policyClient.GetPluginPolicyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(policy);
        return Guard(policyClient, MembershipClient(roleName));
    }

    public static WorkspacePluginGuard Guard(
        IWorkspacePluginPolicyClient policyClient,
        IWorkspaceMembershipClient membershipClient) =>
        new(policyClient, membershipClient, NullLogger<WorkspacePluginGuard>.Instance);

    public static IWorkspaceMembershipClient MembershipClient(string roleName)
    {
        var client = Substitute.For<IWorkspaceMembershipClient>();
        client.GetMembershipAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMembership(IsMember: true, RoleName: roleName, IsActive: true));
        return client;
    }
}
