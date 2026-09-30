using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.API.Hubs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.AssistantService.Tests.Conversations;

/// <summary>
/// Platform-scope WarpBot is for system administrators only, and that is enforced SERVER-SIDE.
///
/// The web widget opens in platform mode only on /admin, but that is presentation. The rule is
/// the WarpTalkSystemAdmin policy on the controller and on the hub's join — the exact platform
/// role "admin", never the workspace "Admin" or "Owner", which a workspace member can hold.
/// </summary>
public class PlatformConversationAuthorizationTests
{
    private static ClaimsPrincipal Principal(Guid userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }

    /// <summary>
    /// The real authorization stack, with the auth service's answer replaced: a token that carries
    /// the platform role "admin" is staff here (with warpbot.use), anyone else is not — which is
    /// what the auth service answers for these fixtures.
    /// </summary>
    private static IAuthorizationService RealAuthorization(Func<Guid, StaffAccess>? access = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddWarpTalkStaffAuthorizationCore();
        services.AddSingleton<IStaffAccessSource>(new DelegateStaffAccessSource(access ?? (id => Granted.Contains(id)
            ? DelegateStaffAccessSource.Staff(AdminPermissions.WarpBotUse)
            : StaffAccess.None)));
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static readonly HashSet<Guid> Granted = [];

    private static ClaimsPrincipal StaffPrincipal(Guid userId)
    {
        Granted.Add(userId);
        return Principal(userId, SystemAdminAuthorization.RoleName);
    }

    [Fact]
    public void TheWholeController_RequiresWarpBotUse()
    {
        var type = typeof(PlatformAssistantConversationsController);
        var authorize = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        Assert.Contains(authorize, a => a is RequirePermissionAttribute { Permission: AdminPermissions.WarpBotUse });
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        // No action may loosen it: an [AllowAnonymous] or a bare [Authorize] on one action would
        // open that action to every signed-in user while the class attribute still reads as safe.
        foreach (var action in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        }
    }

    [Theory]
    [InlineData("Admin")]   // workspace administrator — a different role that differs only in case
    [InlineData("Owner")]   // workspace owner
    [InlineData("Member")]
    [InlineData("user")]
    public async Task TheGate_RefusesEveryWorkspaceRole(string role)
    {
        var result = await RealAuthorization().AuthorizeAsync(
            Principal(Guid.NewGuid(), role), resource: null, new PermissionRequirement(AdminPermissions.WarpBotUse));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task TheGate_AdmitsStaffWithWarpBotUse()
    {
        var result = await RealAuthorization().AuthorizeAsync(
            StaffPrincipal(Guid.NewGuid()), resource: null, new PermissionRequirement(AdminPermissions.WarpBotUse));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task TheGate_RefusesStaffWithoutWarpBotUse_EvenWithTheAdminHint()
    {
        var result = await RealAuthorization(_ => DelegateStaffAccessSource.Staff(AdminPermissions.AuditRead)).AuthorizeAsync(
            Principal(Guid.NewGuid(), SystemAdminAuthorization.RoleName), resource: null, new PermissionRequirement(AdminPermissions.WarpBotUse));

        Assert.False(result.Succeeded);
    }

    // ── the hub: the realtime stream has the same gate as the REST controller ────────────────

    private sealed class HubFixture
    {
        public readonly IAssistantConversationService Workspace = Substitute.For<IAssistantConversationService>();
        public readonly IPlatformAssistantConversationService Platform = Substitute.For<IPlatformAssistantConversationService>();
        public readonly IGroupManager Groups = Substitute.For<IGroupManager>();

        public AssistantHub Hub(ClaimsPrincipal user)
        {
            var context = Substitute.For<HubCallerContext>();
            context.User.Returns(user);
            context.ConnectionId.Returns("connection-1");
            context.ConnectionAborted.Returns(CancellationToken.None);
            var hub = new AssistantHub(Workspace, Platform, RealAuthorization())
            {
                Context = context,
                Groups = Groups,
            };
            return hub;
        }
    }

    [Fact]
    public async Task Hub_RefusesAPlatformConversation_ToItsOwnerWithoutTheAdminRole()
    {
        // A user who was an admin when they started the conversation and has since lost the role.
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var fixture = new HubFixture();
        fixture.Workspace.AuthorizeConversationAccessAsync(conversationId, userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Conversation not found.", ErrorCodes.NotFound));
        fixture.Platform.AuthorizeConversationAccessAsync(conversationId, userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var hub = fixture.Hub(Principal(userId, "Admin"));

        await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(conversationId));
        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
        // Refused on the role, before the platform store is even asked.
        await fixture.Platform.DidNotReceiveWithAnyArgs().AuthorizeConversationAccessAsync(default, default, default);
    }

    [Fact]
    public async Task Hub_RefusesAnotherAdminsPlatformConversation()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var fixture = new HubFixture();
        fixture.Workspace.AuthorizeConversationAccessAsync(conversationId, userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Conversation not found.", ErrorCodes.NotFound));
        fixture.Platform.AuthorizeConversationAccessAsync(conversationId, userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Conversation not found.", ErrorCodes.NotFound));

        var hub = fixture.Hub(StaffPrincipal(userId));

        await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(conversationId));
    }

    [Fact]
    public async Task Hub_JoinsTheAdminToTheirOwnPlatformConversation()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var fixture = new HubFixture();
        fixture.Workspace.AuthorizeConversationAccessAsync(conversationId, userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Conversation not found.", ErrorCodes.NotFound));
        fixture.Platform.AuthorizeConversationAccessAsync(conversationId, userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var hub = fixture.Hub(StaffPrincipal(userId));
        await hub.JoinConversation(conversationId);

        await fixture.Groups.Received(1).AddToGroupAsync(
            "connection-1", AssistantHub.GetConversationGroupName(conversationId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hub_StillJoinsAWorkspaceConversation_WithNoAdminRole()
    {
        // Today's behaviour, unchanged: a member's own workspace conversation needs no platform role.
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var fixture = new HubFixture();
        fixture.Workspace.AuthorizeConversationAccessAsync(conversationId, userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var hub = fixture.Hub(Principal(userId, "Member"));
        await hub.JoinConversation(conversationId);

        await fixture.Groups.Received(1).AddToGroupAsync(
            "connection-1", AssistantHub.GetConversationGroupName(conversationId), Arg.Any<CancellationToken>());
        await fixture.Platform.DidNotReceiveWithAnyArgs().AuthorizeConversationAccessAsync(default, default, default);
    }
}
