using NSubstitute;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The workspace plugin policy semantics themselves. WT-646.
/// </summary>
public class WorkspacePluginGuardTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private const string GoogleDriveKey = "google_drive";
    private const string GoogleCalendarKey = "google_calendar";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullAllowlist_FallsBackToAllowAnyPlugins(bool allowAnyPlugins)
    {
        // Every workspace that predates WT-646 reports this shape, so this is the case that must
        // not have changed at all.
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.LegacyPeer(allowAnyPlugins));

        var result = await guard.CanUseAsync(WorkspaceId, GoogleDriveKey);

        Assert.Equal(allowAnyPlugins, result.IsSuccess);
        if (!allowAnyPlugins)
            Assert.Equal(PluginConstants.ErrorCodes.PermissionDenied, result.ErrorCode);
    }

    [Fact]
    public async Task EmptyAllowlist_PermitsNothing_EvenWhenAllowAnyPluginsIsTrue()
    {
        // The whole reason allowlist_enforced exists on the wire. An empty list is a deliberate
        // "nothing", and it has to outrank a workspace-wide switch that is still set to true -
        // otherwise a workspace could never express "no plugins" through the allowlist at all.
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.WithAllowlist(allowAnyPlugins: true));

        var result = await guard.CanUseAsync(WorkspaceId, GoogleDriveKey);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.WorkspacePluginNotAllowed, result.ErrorCode);
    }

    [Fact]
    public async Task NullAndEmptyAllowlistsDoNotAgree()
    {
        // Stated as one assertion because it is the invariant a `?? new List<string>()` anywhere
        // in this path would quietly destroy, and the failure would look like "plugins stopped
        // working everywhere" rather than like a null-handling bug.
        var noAllowlist = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.LegacyPeer(allowAnyPlugins: true));
        var emptyAllowlist = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.WithAllowlist(allowAnyPlugins: true));

        Assert.True((await noAllowlist.CanUseAsync(WorkspaceId, GoogleDriveKey)).IsSuccess);
        Assert.False((await emptyAllowlist.CanUseAsync(WorkspaceId, GoogleDriveKey)).IsSuccess);
    }

    [Fact]
    public async Task ListedKeyIsPermitted_AndUnlistedKeyIsRefused()
    {
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.WithAllowlist(allowedKeys: GoogleDriveKey));

        Assert.True((await guard.CanUseAsync(WorkspaceId, GoogleDriveKey)).IsSuccess);

        var refused = await guard.CanUseAsync(WorkspaceId, GoogleCalendarKey);
        Assert.False(refused.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.WorkspacePluginNotAllowed, refused.ErrorCode);
    }

    [Fact]
    public async Task AllowlistSupersedesAllowAnyPlugins_SoAListedKeySurvivesTheSwitchBeingOff()
    {
        // The contract calls AllowAnyPlugins "the whole answer when AllowedPluginKeys is null",
        // which it can only be if a configured allowlist takes over from it. So an allowlist is how
        // a workspace with plugins switched off turns a chosen few back on.
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.WithAllowlist(allowAnyPlugins: false, allowedKeys: GoogleDriveKey));

        Assert.True((await guard.CanUseAsync(WorkspaceId, GoogleDriveKey)).IsSuccess);
        Assert.False((await guard.CanUseAsync(WorkspaceId, GoogleCalendarKey)).IsSuccess);
    }

    [Fact]
    public async Task AllowlistKeysAreMatchedExactly()
    {
        // Plugin keys are lower-case identifiers the catalog stores verbatim, and the workspace
        // service does not validate them against the catalog at all. Matching case-insensitively
        // would let a typo'd entry silently widen the allowlist.
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.WithAllowlist(allowedKeys: "Google_Drive"));

        Assert.False((await guard.CanUseAsync(WorkspaceId, GoogleDriveKey)).IsSuccess);
    }

    [Fact]
    public async Task NoWorkspace_PermitsEverythingWithoutAskingTheWorkspaceService()
    {
        // A user outside any workspace context - the personal plugins page - has no workspace
        // policy to answer to, and denying them would take away access that works today.
        var policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
        var membershipClient = Substitute.For<IWorkspaceMembershipClient>();
        var guard = TestWorkspacePluginPolicy.Guard(policyClient, membershipClient);

        Assert.True((await guard.CanUseAsync(null, GoogleDriveKey)).IsSuccess);
        Assert.True((await guard.CanInstallAsync(null, UserId, GoogleDriveKey)).IsSuccess);

        await policyClient.DidNotReceive().GetPluginPolicyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await membershipClient.DidNotReceive()
            .GetMembershipAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemberInstallIsRefused_WhenTheWorkspaceConfinesInstallationToAdmins()
    {
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.MembersMayNotInstall(),
            roleName: "Member");

        var result = await guard.CanInstallAsync(WorkspaceId, UserId, GoogleDriveKey);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.WorkspaceInstallRequiresAdmin, result.ErrorCode);
    }

    [Theory]
    [InlineData(WorkspaceRoleConstants.Owner)]
    [InlineData(WorkspaceRoleConstants.Admin)]
    [InlineData("owner")]
    public async Task OwnerAndAdminMayStillInstall_WhenMemberInstallIsOff(string roleName)
    {
        // Case-insensitively, as every other workspace role check in this codebase does: the role
        // arrives as a display-cased string rather than an enum.
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.MembersMayNotInstall(),
            roleName);

        Assert.True((await guard.CanInstallAsync(WorkspaceId, UserId, GoogleDriveKey)).IsSuccess);
    }

    [Fact]
    public async Task MemberInstallCheckCostsNoRoundTrip_WhenMembersMayInstall()
    {
        var policyClient = Substitute.For<IWorkspacePluginPolicyClient>();
        policyClient.GetPluginPolicyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TestWorkspacePluginPolicy.LegacyPeer(allowAnyPlugins: true));
        var membershipClient = Substitute.For<IWorkspaceMembershipClient>();

        var result = await TestWorkspacePluginPolicy.Guard(policyClient, membershipClient)
            .CanInstallAsync(WorkspaceId, UserId, GoogleDriveKey);

        Assert.True(result.IsSuccess);
        await membershipClient.DidNotReceive()
            .GetMembershipAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllowlistIsCheckedBeforeTheRole_SoAnOwnerCannotInstallAnUnlistedPlugin()
    {
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.WithAllowlist(
                allowMemberPluginInstall: false,
                allowedKeys: GoogleDriveKey),
            WorkspaceRoleConstants.Owner);

        var result = await guard.CanInstallAsync(WorkspaceId, UserId, GoogleCalendarKey);

        Assert.False(result.IsSuccess);
        Assert.Equal(PluginConstants.ErrorCodes.WorkspacePluginNotAllowed, result.ErrorCode);
    }

    [Fact]
    public async Task AnUnreachableWorkspaceServiceDenies()
    {
        // WorkspacePluginPolicySnapshot.Denied is what the gRPC client returns when the workspace
        // service is unreachable or does not know the workspace. It has to read as "no", not as
        // "no policy".
        var guard = TestWorkspacePluginPolicy.Guard(WorkspacePluginPolicySnapshot.Denied);

        var result = await guard.CanUseAsync(WorkspaceId, GoogleDriveKey);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RequirePluginApprovalAloneDoesNotDeny()
    {
        // Deliberate, and the one place worth being explicit about. There is no approval store in
        // this service - plugin_installations has no pending state, no reviewer, no queue and
        // nothing that notifies an Owner - so a workspace that turns this on without an allowlist
        // has asked for a gate nobody could ever pass and no admin could ever open. Denying there
        // would take plugins away with no route to giving them back. The guard logs it instead.
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.LegacyPeer(allowAnyPlugins: true) with
            {
                RequirePluginApproval = true,
            });

        Assert.True((await guard.CanUseAsync(WorkspaceId, GoogleDriveKey)).IsSuccess);
    }

    [Fact]
    public async Task RequirePluginApprovalWithAnAllowlist_IsTheAllowlist()
    {
        // With an allowlist configured, the allowlist IS the approval record: an admin adding a key
        // is the approval, and everything else is already refused. So the flag adds no second check
        // rather than adding a half-built one.
        var guard = TestWorkspacePluginPolicy.Guard(
            TestWorkspacePluginPolicy.WithAllowlist(
                requirePluginApproval: true,
                allowedKeys: GoogleDriveKey));

        Assert.True((await guard.CanUseAsync(WorkspaceId, GoogleDriveKey)).IsSuccess);
        Assert.False((await guard.CanUseAsync(WorkspaceId, GoogleCalendarKey)).IsSuccess);
    }
}
