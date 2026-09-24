using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.Shared.Email;
using WarpTalk.Shared.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The two workspace emails go through the real <see cref="EmailTemplateComposer"/>; only the
/// template store is stubbed. So these prove the send path both falls back to the built-in wording
/// and — the part an admin CMS lives or dies by — actually sends what an admin saved.
/// </summary>
public class WorkspaceInvitationEmailComposerTests
{
    private static (WorkspaceInvitationEmailComposer Composer, Func<SendEmailRequest?> Captured) Build(
        StoredEmailTemplate? stored,
        string templateKey,
        string appBaseUrl = "https://app.warptalk.vn")
    {
        var resendClient = Substitute.For<IResendEmailClient>();
        SendEmailRequest? capturedRequest = null;
        resendClient
            .SendEmailAsync(Arg.Do<SendEmailRequest>(request => capturedRequest = request), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse(true, "message-id", null));

        var source = Substitute.For<IEmailTemplateSource>();
        source.FindActiveAsync(templateKey, Arg.Any<CancellationToken>()).Returns(stored);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppBaseUrl"] = appBaseUrl,
                ["Resend:FromEmail"] = "no-reply@warptalk.vn",
                ["Resend:FromName"] = "WarpTalk"
            })
            .Build();

        var composer = new WorkspaceInvitationEmailComposer(
            resendClient,
            new EmailTemplateComposer(source),
            configuration,
            Substitute.For<ILogger<WorkspaceInvitationEmailComposer>>());
        return (composer, () => capturedRequest);
    }

    private static Workspace Team() => new() { Id = Guid.NewGuid(), Name = "WarpTalk Team", Slug = "warptalk-team" };

    [Fact]
    public async Task SendInvitationEmailAsync_WithNoStoredTemplate_SendsTheDefaultWithTheAcceptUrl()
    {
        var (composer, captured) = Build(null, EmailTemplateCatalog.WorkspaceInvitation);

        await composer.SendInvitationEmailAsync(
            new WorkspaceInvitation { Id = Guid.NewGuid(), Email = "invitee@warptalk.vn" },
            Team(),
            "Real Inviter",
            "Member",
            "abc123",
            CancellationToken.None);

        var request = captured();
        Assert.NotNull(request);
        Assert.Equal("You've been invited to join WarpTalk Team on WarpTalk", request!.Subject);
        Assert.Contains("https://app.warptalk.vn/login?token=abc123", request.HtmlBody);
        Assert.Contains("https://app.warptalk.vn/login?token=abc123", request.TextBody);
        Assert.Contains("Real Inviter", request.HtmlBody);
    }

    [Fact]
    public async Task SendInvitationEmailAsync_SendsTheTemplateAnAdminSaved()
    {
        var stored = new StoredEmailTemplate(
            "Join {{WorkspaceName}} — edited by the CMS",
            "Welcome aboard",
            "<p>CMS body for {{InviterName}}</p><a href=\"{{JoinUrl}}\">Join now</a>",
            Version: 3);
        var (composer, captured) = Build(stored, EmailTemplateCatalog.WorkspaceInvitation);

        await composer.SendInvitationEmailAsync(
            new WorkspaceInvitation { Id = Guid.NewGuid(), Email = "invitee@warptalk.vn" },
            Team(),
            "Real Inviter",
            "Member",
            "abc123",
            CancellationToken.None);

        var request = captured();
        Assert.NotNull(request);
        Assert.Equal("Join WarpTalk Team — edited by the CMS", request!.Subject);
        Assert.Contains("Welcome aboard", request.HtmlBody);
        Assert.Contains("<p>CMS body for Real Inviter</p>", request.HtmlBody);
        Assert.Contains("href=\"https://app.warptalk.vn/login?token=abc123\"", request.HtmlBody);
        Assert.DoesNotContain("Accept &amp; Join Workspace", request.HtmlBody);
        Assert.Contains("Join now (https://app.warptalk.vn/login?token=abc123)", request.TextBody);
    }

    [Fact]
    public async Task SendJoinRequestApprovedEmailAsync_ShouldBuildWorkspaceUrlWithFinalMembershipType()
    {
        var (composer, captured) = Build(null, EmailTemplateCatalog.WorkspaceJoinRequestApproved, "https://app.warptalk.vn/");
        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            Email = "requester@partner.vn",
            MembershipType = MembershipType.External.ToString()
        };

        await composer.SendJoinRequestApprovedEmailAsync(
            invitation,
            new Workspace { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" },
            CancellationToken.None);

        var request = captured();
        Assert.NotNull(request);
        Assert.Equal("Your request to join Acme was approved", request!.Subject);
        Assert.Contains("https://app.warptalk.vn/acme/home", request.HtmlBody);
        Assert.Contains("https://app.warptalk.vn/acme/home", request.TextBody);
        Assert.Contains(MembershipType.External.ToString(), request.HtmlBody);
        Assert.Contains(MembershipType.External.ToString(), request.TextBody);
    }

    [Fact]
    public async Task SendInvitationEmailAsync_EncodesAWorkspaceNameThatLooksLikeMarkup()
    {
        var (composer, captured) = Build(null, EmailTemplateCatalog.WorkspaceInvitation);

        await composer.SendInvitationEmailAsync(
            new WorkspaceInvitation { Id = Guid.NewGuid(), Email = "invitee@warptalk.vn" },
            new Workspace { Id = Guid.NewGuid(), Name = "<img src=x onerror=alert(1)>", Slug = "x" },
            "Real Inviter",
            "Member",
            "abc123",
            CancellationToken.None);

        var request = captured();
        Assert.NotNull(request);
        Assert.DoesNotContain("<img src=x", request!.HtmlBody);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", request.HtmlBody);
    }
}
