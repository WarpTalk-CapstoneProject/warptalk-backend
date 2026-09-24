using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.NotificationService.API.GrpcServices;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Email;
using WarpTalk.Shared.Protos;
using NotificationServiceImpl = WarpTalk.NotificationService.Application.Services.NotificationService;

namespace WarpTalk.NotificationService.Tests.Application.EmailTemplates;

/// <summary>
/// The failure this CMS must not have: an admin saves, the save succeeds, and every email keeps
/// saying what it said before because no sender reads what was saved.
///
/// Each test here saves through <see cref="AdminEmailTemplateService"/> — the exact code behind
/// PUT /admin/notifications/email-templates/{key} — and then reads the result back through the
/// path a sender uses: the GetEmailTemplate RPC the other services call, the composer they build
/// the email with, and this service's own email copy of a notification.
/// </summary>
public sealed class EmailTemplateSendPathTests
{
    private static readonly Guid AdminId = Guid.NewGuid();

    private readonly InMemoryTemplateStore _store = new();

    private AdminEmailTemplateService Admin() =>
        new(_store.UnitOfWork.Object, NullLogger<AdminEmailTemplateService>.Instance);

    [Fact]
    public async Task ASavedTemplate_IsWhatTheGrpcReadServesToOtherServices()
    {
        var saved = await Admin().SaveAsync(AdminId, EmailTemplateCatalog.AuthVerifyEmail, new SaveEmailTemplateRequest(
            "Confirm {{FullName}}'s address", "One more step", "<p>Tap below.</p><a href=\"{{VerifyUrl}}\">Confirm</a>"));
        Assert.True(saved.IsSuccess, saved.Error);

        var grpc = new NotificationGrpcServiceImpl(
            Mock.Of<INotificationService>(),
            Mock.Of<StackExchange.Redis.IConnectionMultiplexer>(),
            NullLogger<NotificationGrpcServiceImpl>.Instance,
            new DbEmailTemplateSource(_store.UnitOfWork.Object));

        var response = await grpc.GetEmailTemplate(
            new GetEmailTemplateRequest { TemplateKey = EmailTemplateCatalog.AuthVerifyEmail },
            Mock.Of<ServerCallContext>());

        Assert.True(response.Found);
        Assert.Equal("Confirm {{FullName}}'s address", response.Subject);
        Assert.Equal("One more step", response.Heading);
        Assert.Contains("Tap below.", response.BodyHtml);
        Assert.Equal(1, response.Version);
    }

    [Fact]
    public async Task ASavedTemplate_IsWhatTheComposerSends_AndAResetGoesBackToTheDefault()
    {
        var admin = Admin();
        var composer = new EmailTemplateComposer(new DbEmailTemplateSource(_store.UnitOfWork.Object));
        var values = new Dictionary<string, string>
        {
            ["ParticipantName"] = "Linh",
            ["MeetingTitle"] = "Q3 review",
            ["ScheduledTime"] = "Monday 10:00",
            ["MeetingLink"] = "https://app.warptalk.vn/room/abc",
        };

        var before = await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, values);
        Assert.Equal("Invitation to Meeting: Q3 review", before.Subject);

        var saved = await admin.SaveAsync(AdminId, EmailTemplateCatalog.MeetingInvitation, new SaveEmailTemplateRequest(
            "{{MeetingTitle}} — you're invited", "Join us", "<p>At {{ScheduledTime}}.</p><a href=\"{{MeetingLink}}\">Join</a>"));
        Assert.True(saved.IsSuccess, saved.Error);

        var after = await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, values);
        Assert.Equal("Q3 review — you're invited", after.Subject);
        Assert.Contains("<p>At Monday 10:00.</p>", after.HtmlBody);
        Assert.Contains("Join (https://app.warptalk.vn/room/abc)", after.TextBody);

        var reset = await admin.ResetAsync(AdminId, EmailTemplateCatalog.MeetingInvitation);
        Assert.True(reset.IsSuccess, reset.Error);

        var afterReset = await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, values);
        Assert.Equal("Invitation to Meeting: Q3 review", afterReset.Subject);
    }

    [Fact]
    public async Task ASavedTemplate_IsWhatTheNotificationEmailCopySends()
    {
        var saved = await Admin().SaveAsync(AdminId, EmailTemplateCatalog.NotificationEmailCopy, new SaveEmailTemplateRequest(
            "[WarpTalk] {{Title}}", "{{Title}}", "<p>CMS copy: {{Content}}</p><a href=\"{{ActionUrl}}\">Open</a>"));
        Assert.True(saved.IsSuccess, saved.Error);

        var userId = Guid.NewGuid();
        var preferences = new Mock<INotificationPreferenceRepository>();
        preferences.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPreference { UserId = userId, EmailEnabled = true });
        _store.UnitOfWork.Setup(u => u.NotificationPreferenceRepository).Returns(preferences.Object);
        _store.UnitOfWork.Setup(u => u.NotificationMessageRepository).Returns(Mock.Of<INotificationMessageRepository>());

        EmailMessage? sent = null;
        var sender = new Mock<IEmailSender>();
        sender.Setup(s => s.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((message, _) => sent = message)
            .ReturnsAsync(true);

        // Built the way DI builds it: the composer is not passed, so the service reads its own store.
        var notifications = new NotificationServiceImpl(
            _store.UnitOfWork.Object, NullLogger<NotificationServiceImpl>.Instance, sender.Object);

        await notifications.CreateNotificationAsync(new CreateNotificationMessageDto(
            userId, "SYSTEM", "Summary ready", "Line one\nLine two", "javascript:alert(1)",
            "{\"toEmail\":\"linh@example.com\"}"));

        Assert.NotNull(sent);
        Assert.Equal("linh@example.com", sent!.ToEmail);
        Assert.Equal("[WarpTalk] Summary ready", sent.Subject);
        Assert.Contains("<p>CMS copy: Line one<br />Line two</p>", sent.HtmlBody);
        Assert.DoesNotContain("javascript:", sent.HtmlBody);
        Assert.Contains($"href=\"{NotificationServiceImpl.FallbackActionUrl}\"", sent.HtmlBody);
    }

    [Fact]
    public async Task TheDefaultNotificationCopy_EncodesContentAndRefusesAnUnsafeLink()
    {
        var userId = Guid.NewGuid();
        var preferences = new Mock<INotificationPreferenceRepository>();
        preferences.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPreference { UserId = userId, EmailEnabled = true });
        _store.UnitOfWork.Setup(u => u.NotificationPreferenceRepository).Returns(preferences.Object);
        _store.UnitOfWork.Setup(u => u.NotificationMessageRepository).Returns(Mock.Of<INotificationMessageRepository>());

        EmailMessage? sent = null;
        var sender = new Mock<IEmailSender>();
        sender.Setup(s => s.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((message, _) => sent = message)
            .ReturnsAsync(true);

        var notifications = new NotificationServiceImpl(
            _store.UnitOfWork.Object, NullLogger<NotificationServiceImpl>.Instance, sender.Object);
        await notifications.CreateNotificationAsync(new CreateNotificationMessageDto(
            userId, "SYSTEM", "<script>alert('title')</script>", "Hello <img src=x onerror=alert(1)>", "javascript:alert(1)",
            "{\"email\":\"linh@example.com\"}"));

        Assert.NotNull(sent);
        Assert.DoesNotContain("<script>", sent!.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", sent.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", sent.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://warptalk.app", sent.HtmlBody);
    }
}
