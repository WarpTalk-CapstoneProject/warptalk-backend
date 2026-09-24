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

    private static IAuthorizationService RealAuthorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddWarpTalkSystemAdminAuthorization();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    [Fact]
    public void TheWholeController_IsBehindTheSystemAdminPolicy()
    {
        var type = typeof(PlatformAssistantConversationsController);
        var authorize = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        Assert.Contains(authorize, a => a.Policy == SystemAdminAuthorization.PolicyName);
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        // No action may loosen it: an [AllowAnonymous] or a bare [Authorize] on one action would
        // open that action to every signed-in user while the class attribute still reads as safe.
        foreach (var action in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.All(
                action.GetCustomAttributes<AuthorizeAttribute>(inherit: true),
                a => Assert.Equal(SystemAdminAuthorization.PolicyName, a.Policy));
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
            Principal(Guid.NewGuid(), role), SystemAdminAuthorization.PolicyName);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task TheGate_AdmitsThePlatformSystemAdmin()
    {
        var result = await RealAuthorization().AuthorizeAsync(
            Principal(Guid.NewGuid(), SystemAdminAuthorization.RoleName), SystemAdminAuthorization.PolicyName);

        Assert.True(result.Succeeded);
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

        var hub = fixture.Hub(Principal(userId, SystemAdminAuthorization.RoleName));

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

        var hub = fixture.Hub(Principal(userId, SystemAdminAuthorization.RoleName));
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
