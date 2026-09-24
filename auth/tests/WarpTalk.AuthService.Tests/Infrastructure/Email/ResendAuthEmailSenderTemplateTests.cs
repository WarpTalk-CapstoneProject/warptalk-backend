using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Infrastructure.Services;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Email;
using WarpTalk.Shared.Interfaces;
using Xunit;

namespace WarpTalk.AuthService.Tests.Infrastructure.Email;

/// <summary>
/// The account emails go through the real composer with only the template store stubbed: the
/// built-in wording when nothing is stored, and what an admin saved when something is.
/// </summary>
public class ResendAuthEmailSenderTemplateTests
{
    private static readonly User Linh = new() { Id = Guid.NewGuid(), Email = "linh@example.com", FullName = "Linh <Nguyen>" };

    private static (ResendAuthEmailSender Sender, Func<SendEmailRequest?> Captured) Build(string key, StoredEmailTemplate? stored)
    {
        var resend = Substitute.For<IResendEmailClient>();
        SendEmailRequest? captured = null;
        resend.SendEmailAsync(Arg.Do<SendEmailRequest>(request => captured = request), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse(true, "id", null));

        var source = Substitute.For<IEmailTemplateSource>();
        source.FindActiveAsync(key, Arg.Any<CancellationToken>()).Returns(stored);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AppBaseUrl"] = "https://app.warptalk.vn/" })
            .Build();

        var sender = new ResendAuthEmailSender(
            resend,
            new EmailTemplateComposer(source),
            Options.Create(new ResendSettings { FromEmail = "no-reply@warptalk.vn", FromName = "WarpTalk" }),
            configuration);
        return (sender, () => captured);
    }

    [Fact]
    public async Task Verification_WithNoStoredTemplate_SendsTheDefault()
    {
        var (sender, captured) = Build(EmailTemplateCatalog.AuthVerifyEmail, null);

        await sender.SendVerificationEmailAsync(Linh, "tok en");

        var request = captured()!;
        Assert.Equal("Verify your WarpTalk email", request.Subject);
        Assert.Equal("linh@example.com", request.To);
        Assert.Contains("https://app.warptalk.vn/verify-email?token=tok%20en", request.HtmlBody);
        Assert.Contains("https://app.warptalk.vn/verify-email?token=tok%20en", request.TextBody);
        Assert.Contains("Linh &lt;Nguyen&gt;", request.HtmlBody);
    }

    [Fact]
    public async Task PasswordReset_SendsTheTemplateAnAdminSaved()
    {
        var (sender, captured) = Build(EmailTemplateCatalog.AuthPasswordReset, new StoredEmailTemplate(
            "Reset it, {{FullName}}",
            "New password",
            "<p>Edited in the CMS.</p><a href=\"{{ResetUrl}}\">Reset</a>",
            Version: 5));

        await sender.SendPasswordResetEmailAsync(Linh, "abc");

        var request = captured()!;
        Assert.Equal("Reset it, Linh <Nguyen>", request.Subject);
        Assert.Contains("<p>Edited in the CMS.</p>", request.HtmlBody);
        Assert.Contains("href=\"https://app.warptalk.vn/reset-password?token=abc\"", request.HtmlBody);
        Assert.DoesNotContain("We received a request to reset", request.HtmlBody);
    }

    [Fact]
    public async Task PasswordReset_WhenTheStoreIsDown_StillSendsTheDefault()
    {
        var resend = Substitute.For<IResendEmailClient>();
        SendEmailRequest? captured = null;
        resend.SendEmailAsync(Arg.Do<SendEmailRequest>(request => captured = request), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse(true, "id", null));
        var source = Substitute.For<IEmailTemplateSource>();
        source.FindActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<StoredEmailTemplate?>>(_ => throw new InvalidOperationException("store down"));

        var sender = new ResendAuthEmailSender(
            resend,
            new EmailTemplateComposer(source),
            Options.Create(new ResendSettings { FromEmail = "no-reply@warptalk.vn", FromName = "WarpTalk" }),
            new ConfigurationBuilder().Build());

        await sender.SendPasswordResetEmailAsync(Linh, "abc");

        Assert.Equal("Reset your WarpTalk password", captured!.Subject);
    }
}
