using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.NotificationService.API.GrpcServices;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services.EmailCms;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Email;
using WarpTalk.Shared.Protos;
using NotificationServiceImpl = WarpTalk.NotificationService.Application.Services.NotificationService;

namespace WarpTalk.NotificationService.Tests.Application.EmailTemplates;

/// <summary>
/// The two promises of the email CMS, proved against the paths a send takes:
///
///  1. What is published is what is sent — through the GetEmailTemplate RPC other services call,
///     the composer every sender builds with, and this service's own email copy.
///  2. A draft is never sent. Saving a draft changes nothing any recipient receives until Publish.
///
/// Layouts, blocks and locales ride the same path, so each is proved here too.
/// </summary>
public sealed class EmailCmsSendPathTests
{
    private readonly InMemoryEmailCmsStore _store = new();

    private EmailContentService Content() =>
        new(_store.UnitOfWork.Object, NullLogger<EmailContentService>.Instance);

    private EmailBlockService Blocks() =>
        new(_store.UnitOfWork.Object, NullLogger<EmailBlockService>.Instance);

    private EmailTemplateComposer Composer() => new(new EmailPublishedResolver(_store.UnitOfWork.Object));

    private static readonly Dictionary<string, string> InviteValues = new()
    {
        ["ParticipantName"] = "Linh",
        ["MeetingTitle"] = "Q3 review",
        ["ScheduledTime"] = "Monday 10:00",
        ["MeetingLink"] = "https://app.warptalk.vn/room/abc",
    };

    private static SaveEmailDraftRequest InviteDraft(string subject, Guid? layout = null, string body = "<p>At {{ScheduledTime}}.</p><a href=\"{{MeetingLink}}\">Join</a>") =>
        new(subject, "Preview line for {{MeetingTitle}}", "Join us", body, null, layout);

    private NotificationGrpcServiceImpl Grpc() =>
        new(Mock.Of<INotificationService>(), Mock.Of<StackExchange.Redis.IConnectionMultiplexer>(),
            NullLogger<NotificationGrpcServiceImpl>.Instance,
            new EmailPublishedResolver(_store.UnitOfWork.Object),
            new DbEmailDeliveryRecorder(_store.UnitOfWork.Object, NullLogger<DbEmailDeliveryRecorder>.Instance));

    [Fact]
    public async Task ADraft_IsNeverSent_UntilItIsPublished()
    {
        var content = Content();
        var composer = Composer();

        var saved = await content.SaveDraftAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "en",
            InviteDraft("{{MeetingTitle}} — you're invited"));
        Assert.True(saved.IsSuccess, saved.Error);

        var beforePublish = await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, InviteValues);
        Assert.Equal("Invitation to Meeting: Q3 review", beforePublish.Subject);

        var published = await content.PublishAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "en", new PublishEmailRequest("first cut"));
        Assert.True(published.IsSuccess, published.Error);

        var afterPublish = await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, InviteValues);
        Assert.Equal("Q3 review — you're invited", afterPublish.Subject);
        Assert.Contains("<p>At Monday 10:00.</p>", afterPublish.HtmlBody);
        Assert.Contains("Preview line for Q3 review", afterPublish.HtmlBody);
        Assert.Equal(1, afterPublish.Version);

        // Editing again changes nothing sent until the next publish.
        await content.SaveDraftAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "en", InviteDraft("Second draft"));
        var stillFirst = await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, InviteValues);
        Assert.Equal("Q3 review — you're invited", stillFirst.Subject);
    }

    [Fact]
    public async Task TheGrpcRead_ServesThePublishedVersionInTheRequestedLocale_WithFallback()
    {
        var content = Content();
        await content.SaveDraftAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.AuthVerifyEmail, "en",
            new SaveEmailDraftRequest("Confirm {{FullName}}", "", "One more step", "<a href=\"{{VerifyUrl}}\">Confirm</a>", null, null));
        await content.PublishAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.AuthVerifyEmail, "en", new PublishEmailRequest());
        await content.SaveDraftAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.AuthVerifyEmail, "vi",
            new SaveEmailDraftRequest("Xác nhận {{FullName}}", "", "Còn một bước", "<a href=\"{{VerifyUrl}}\">Xác nhận</a>", null, null));
        await content.PublishAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.AuthVerifyEmail, "vi", new PublishEmailRequest());

        var grpc = Grpc();
        var vi = await grpc.GetEmailTemplate(new GetEmailTemplateRequest { TemplateKey = EmailTemplateCatalog.AuthVerifyEmail, Locale = "vi-VN" }, Mock.Of<ServerCallContext>());
        var ja = await grpc.GetEmailTemplate(new GetEmailTemplateRequest { TemplateKey = EmailTemplateCatalog.AuthVerifyEmail, Locale = "ja" }, Mock.Of<ServerCallContext>());
        var none = await grpc.GetEmailTemplate(new GetEmailTemplateRequest { TemplateKey = EmailTemplateCatalog.AuthPasswordReset }, Mock.Of<ServerCallContext>());

        Assert.True(vi.Found);
        Assert.Equal("vi", vi.Locale);
        Assert.Equal("Xác nhận {{FullName}}", vi.Subject);
        Assert.True(ja.Found);
        Assert.Equal("en", ja.Locale);
        Assert.Equal("Confirm {{FullName}}", ja.Subject);
        Assert.False(none.Found);

        // And a sender reading it through the shared source renders the Vietnamese one.
        var stored = GrpcEmailTemplateSource.FromResponse(vi);
        var email = await new EmailTemplateComposer(new FixedSource(stored)).ComposeAsync(EmailTemplateCatalog.AuthVerifyEmail,
            new Dictionary<string, string> { ["FullName"] = "Linh", ["VerifyUrl"] = "https://x.test/v" }, "vi");
        Assert.Equal("Xác nhận Linh", email.Subject);
        Assert.Equal("vi", email.Locale);
    }

    [Fact]
    public async Task ArchivingALocale_FallsBackToEnglish_AndThenToTheBuiltInWording()
    {
        var content = Content();
        var composer = Composer();
        await content.SaveDraftAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "vi", InviteDraft("VI {{MeetingTitle}}"));
        await content.PublishAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "vi", new PublishEmailRequest());

        Assert.Equal("VI Q3 review", (await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, InviteValues, "vi")).Subject);

        await content.ArchiveAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "vi");

        Assert.Equal("Invitation to Meeting: Q3 review", (await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, InviteValues, "vi")).Subject);
    }

    [Fact]
    public async Task APublishedLayout_WrapsEverySend_AndItsDraftDoesNot()
    {
        var blocks = Blocks();
        var created = await blocks.CreateAsync(InMemoryEmailCmsStore.Admin, new CreateEmailBlockRequest(
            "LAYOUT", "brand-2026", "Brand 2026", null,
            "<html><head></head><body><div class=\"brand-2026\">{{#heading}}<h2>{{heading}}</h2>{{/heading}}{{content}}</div></body></html>",
            "{{content}}\n-- WarpTalk 2026", ".brand-2026 { background: #000 !important; }"));
        Assert.True(created.IsSuccess, created.Error);

        var composer = Composer();
        Assert.DoesNotContain("brand-2026", (await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, InviteValues)).HtmlBody);

        var published = await blocks.PublishAsync(InMemoryEmailCmsStore.Admin, created.Value!.Id, new PublishEmailRequest());
        Assert.True(published.IsSuccess, published.Error);
        Assert.True(published.Value!.IsDefault); // the first layout published becomes the default

        var email = await composer.ComposeAsync(EmailTemplateCatalog.MeetingInvitation, InviteValues);
        Assert.Contains("<div class=\"brand-2026\">", email.HtmlBody);
        Assert.Contains("<h2>WarpTalk Meeting Invitation</h2>", email.HtmlBody);
        Assert.Contains("@media (prefers-color-scheme: dark)", email.HtmlBody);
        Assert.EndsWith("-- WarpTalk 2026", email.TextBody);
    }

    [Fact]
    public async Task APublishedBlock_IsExpandedIntoEverySendThatIncludesIt()
    {
        var blocks = Blocks();
        var signature = await blocks.CreateAsync(InMemoryEmailCmsStore.Admin, new CreateEmailBlockRequest(
            "PARTIAL", "signature", "Signature", null, "<p class=\"sig\">— The WarpTalk team</p>", null, null));
        await blocks.PublishAsync(InMemoryEmailCmsStore.Admin, signature.Value!.Id, new PublishEmailRequest());

        var content = Content();
        await content.SaveDraftAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "en",
            InviteDraft("Invite", body: "<a href=\"{{MeetingLink}}\">Join</a>{{> signature}}"));
        var published = await content.PublishAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "en", new PublishEmailRequest());
        Assert.True(published.IsSuccess, published.Error);

        var email = await Composer().ComposeAsync(EmailTemplateCatalog.MeetingInvitation, InviteValues);
        Assert.Contains("<p class=\"sig\">— The WarpTalk team</p>", email.HtmlBody);
        Assert.DoesNotContain("{{> signature}}", email.HtmlBody);
    }

    [Fact]
    public async Task ABlockEdit_ThatWouldBreakAPublishedEmail_IsRefused()
    {
        var blocks = Blocks();
        var signature = await blocks.CreateAsync(InMemoryEmailCmsStore.Admin, new CreateEmailBlockRequest(
            "PARTIAL", "signature", "Signature", null, "<p>Team</p>", null, null));
        await blocks.PublishAsync(InMemoryEmailCmsStore.Admin, signature.Value!.Id, new PublishEmailRequest());
        var content = Content();
        await content.SaveDraftAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "en",
            InviteDraft("Invite", body: "<a href=\"{{MeetingLink}}\">Join</a>{{> signature}}"));
        await content.PublishAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.MeetingInvitation, "en", new PublishEmailRequest());

        await blocks.SaveDraftAsync(InMemoryEmailCmsStore.Admin, signature.Value.Id,
            new SaveEmailBlockDraftRequest("Signature", null, "<p>Hi {{VerifyUrl}}</p>", null, null));
        var publish = await blocks.PublishAsync(InMemoryEmailCmsStore.Admin, signature.Value.Id, new PublishEmailRequest());

        Assert.False(publish.IsSuccess);
        Assert.Contains("Meeting invitation", publish.Error);
        Assert.Contains("VerifyUrl", publish.Error);
    }

    [Fact]
    public async Task TheNotificationEmailCopy_SendsThePublishedVersion_AndIsCounted()
    {
        var content = Content();
        await content.SaveDraftAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.NotificationEmailCopy, "en",
            new SaveEmailDraftRequest("[WarpTalk] {{Title}}", "", "{{Title}}", "<p>CMS copy: {{Content}}</p><a href=\"{{ActionUrl}}\">Open</a>", null, null));
        await content.PublishAsync(InMemoryEmailCmsStore.Admin, EmailTemplateCatalog.NotificationEmailCopy, "en", new PublishEmailRequest());

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
            _store.UnitOfWork.Object, NullLogger<NotificationServiceImpl>.Instance, sender.Object,
            deliveries: new DbEmailDeliveryRecorder(_store.UnitOfWork.Object, NullLogger<DbEmailDeliveryRecorder>.Instance));

        await notifications.CreateNotificationAsync(new CreateNotificationMessageDto(
            userId, "SYSTEM", "Summary ready", "Line one\nLine two", "javascript:alert(1)", "{\"toEmail\":\"linh@example.com\"}"));

        Assert.NotNull(sent);
        Assert.Equal("[WarpTalk] Summary ready", sent!.Subject);
        Assert.Contains("<p>CMS copy: Line one<br />Line two</p>", sent.HtmlBody);
        Assert.DoesNotContain("javascript:", sent.HtmlBody);
        var stat = Assert.Single(_store.Stats);
        Assert.Equal((EmailTemplateCatalog.NotificationEmailCopy, "en", 1, 0), (stat.TemplateKey, stat.Locale, stat.SentCount, stat.FailedCount));
    }

    [Fact]
    public async Task OtherServicesReportTheirSends_OverTheRecordEmailDeliveryRpc()
    {
        var grpc = Grpc();
        var context = Mock.Of<ServerCallContext>();

        await grpc.RecordEmailDelivery(new RecordEmailDeliveryRequest { TemplateKey = EmailTemplateCatalog.AuthVerifyEmail, Locale = "vi", Succeeded = true }, context);
        await grpc.RecordEmailDelivery(new RecordEmailDeliveryRequest { TemplateKey = EmailTemplateCatalog.AuthVerifyEmail, Locale = "vi", Succeeded = false }, context);
        var unknown = await grpc.RecordEmailDelivery(new RecordEmailDeliveryRequest { TemplateKey = "billing.invoice", Succeeded = true }, context);

        var stat = Assert.Single(_store.Stats);
        Assert.Equal((1, 1), (stat.SentCount, stat.FailedCount));
        Assert.False(unknown.Recorded);

        var stats = await Content().GetStatsAsync(EmailTemplateCatalog.AuthVerifyEmail, 7);
        Assert.Equal(1, stats.Value!.Totals.Sent);
        Assert.Equal(1, stats.Value.Totals.Failed);
        Assert.Equal(7, stats.Value.Daily.Count);
        Assert.Equal("vi", Assert.Single(stats.Value.ByLocale).Locale);
    }

    private sealed class FixedSource(StoredEmailTemplate template) : IEmailTemplateSource
    {
        public Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, string? locale, CancellationToken ct = default) =>
            Task.FromResult<StoredEmailTemplate?>(template);
    }
}
