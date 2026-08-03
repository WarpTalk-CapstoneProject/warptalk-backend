using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.Shared.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Adapters;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceInvitationEmailComposerTests
{
    [Fact]
    public async Task SendInvitationEmailAsync_ShouldBuildAcceptUrlWithInvitationToken()
    {
        var resendClient = Substitute.For<IResendEmailClient>();
        SendEmailRequest? capturedRequest = null;
        resendClient
            .SendEmailAsync(Arg.Do<SendEmailRequest>(request => capturedRequest = request), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse(true, "message-id", null));
        var templateProvider = Substitute.For<IEmailTemplateProvider>();
        templateProvider
            .GetTemplateAsync("workspace-invitation-email", Arg.Any<CancellationToken>())
            .Returns("<a href=\"{{JoinUrl}}\">Accept</a>");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppBaseUrl"] = "https://app.warptalk.vn",
                ["Resend:FromEmail"] = "no-reply@warptalk.vn",
                ["Resend:FromName"] = "WarpTalk"
            })
            .Build();
        var composer = new WorkspaceInvitationEmailComposer(
            resendClient,
            templateProvider,
            configuration,
            Substitute.For<ILogger<WorkspaceInvitationEmailComposer>>());
        var invitation = new WorkspaceInvitation
        {
            Id = Guid.NewGuid(),
            Email = "invitee@warptalk.vn"
        };
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "WarpTalk Team",
            Slug = "warptalk-team"
        };

        await composer.SendInvitationEmailAsync(
            invitation,
            workspace,
            "Real Inviter",
            "Member",
            "abc123",
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Contains("https://app.warptalk.vn/invitations/abc123", capturedRequest!.HtmlBody);
        Assert.Contains("https://app.warptalk.vn/invitations/abc123", capturedRequest.TextBody);
    }
}
