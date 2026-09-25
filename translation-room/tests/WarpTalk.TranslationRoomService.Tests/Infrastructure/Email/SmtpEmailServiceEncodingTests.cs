using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Email;
using WarpTalk.Shared.Services;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure.Email;

/// <summary>
/// The meeting title is typed by the host and mailed to everyone they invite. It used to be
/// interpolated into the HTML raw, so a title could put its own links in somebody else's inbox
/// under WarpTalk's name. The wording is now an admin-editable template; the encoding moved into
/// the shared renderer and these hold it there.
/// </summary>
public class SmtpEmailServiceEncodingTests
{
    private const string HostileTitle = "<a href=\"https://evil\">x</a>";
    private const string Link = "https://app.warptalk.test/room/5f0c1e9a-7a2b-4c1e-9a3f-2b1d0c9e8f7a";

    private static SmtpEmailService Service(IEmailTemplateSource? source = null) => new(
        Options.Create(new SmtpSettings { FromEmail = "noreply@warptalk.test", FromName = "WarpTalk" }),
        new EmailTemplateComposer(source ?? new DefaultEmailTemplateSource()),
        NullLogger<SmtpEmailService>.Instance);

    private readonly SmtpEmailService _service = Service();

    [Fact]
    public async Task Invitation_EncodesHostileTitle()
    {
        var html = (await _service.BuildMeetingInvitationMessageAsync("guest@example.com", "Participant", Link, HostileTitle, "2026-09-16 10:00")).HtmlBody;

        html.Should().Contain("&lt;a href=&quot;https://evil&quot;&gt;x&lt;/a&gt;");
        html.Should().NotContain("https://evil\"");
        html.Should().NotContain("<a href=\"https://evil");
    }

    [Fact]
    public async Task Invitation_EncodesNameAndTime()
    {
        var html = (await _service.BuildMeetingInvitationMessageAsync(
            "guest@example.com", "<img src=x onerror=alert(1)>", Link, "Sync", "<b>tomorrow</b>")).HtmlBody;

        html.Should().Contain("&lt;img src=x onerror=alert(1)&gt;");
        html.Should().Contain("&lt;b&gt;tomorrow&lt;/b&gt;");
        html.Should().NotContain("<img");
        html.Should().NotContain("<b>tomorrow");
    }

    [Fact]
    public async Task Reminder_EncodesHostileTitleNameAndStartsIn()
    {
        var html = (await _service.BuildMeetingReminderMessageAsync(
            "guest@example.com", "<i>Eve</i>", Link, HostileTitle, "<u>5 minutes</u>")).HtmlBody;

        html.Should().Contain("&lt;a href=&quot;https://evil&quot;&gt;x&lt;/a&gt;");
        html.Should().Contain("&lt;i&gt;Eve&lt;/i&gt;");
        html.Should().Contain("&lt;u&gt;5 minutes&lt;/u&gt;");
        html.Should().NotContain("<a href=\"https://evil");
    }

    [Fact]
    public async Task Subject_KeepsTitleAsPlainText()
    {
        // A header is not HTML: encoding it would show recipients "&lt;a href...".
        (await _service.BuildMeetingInvitationMessageAsync("guest@example.com", "Participant", Link, HostileTitle, "soon"))
            .Subject.Should().Be($"Invitation to Meeting: {HostileTitle}");
        (await _service.BuildMeetingReminderMessageAsync("guest@example.com", "Participant", Link, HostileTitle, "5 minutes"))
            .Subject.Should().Be($"Reminder: Meeting '{HostileTitle}' starts in 5 minutes");
    }

    [Fact]
    public async Task Subject_CannotInjectHeaders()
    {
        var message = await _service.BuildMeetingInvitationMessageAsync(
            "guest@example.com", "Participant", Link, "Sync\r\nBcc: attacker@evil.test", "soon");

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        var reparsed = MimeMessage.Load(stream);

        reparsed.Bcc.Count.Should().Be(0);
        reparsed.Headers.Contains(HeaderId.Bcc).Should().BeFalse();
    }

    [Fact]
    public async Task Link_IsAttributeEncoded()
    {
        var html = (await _service.BuildMeetingInvitationMessageAsync(
            "guest@example.com", "Participant", "https://app.warptalk.test/room/1?a=1&b='x'", "Sync", "soon")).HtmlBody;

        html.Should().Contain("href=\"https://app.warptalk.test/room/1?a=1&amp;b=&#39;x&#39;\"");
        html.Should().NotContain("b='x'");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("/room/123")]
    [InlineData("")]
    public async Task NonWebLink_IsRefused(string link)
    {
        var invitation = () => _service.BuildMeetingInvitationMessageAsync("guest@example.com", "Participant", link, "Sync", "soon");
        var reminder = () => _service.BuildMeetingReminderMessageAsync("guest@example.com", "Participant", link, "Sync", "5 minutes");

        await invitation.Should().ThrowAsync<ArgumentException>();
        await reminder.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Invitation_SendsTheTemplateAnAdminSaved()
    {
        var source = new FixedTemplateSource(EmailTemplateCatalog.MeetingInvitation, new StoredEmailTemplate(
            "You're on the list: {{MeetingTitle}}",
            "See you there",
            "<p>Custom CMS body at {{ScheduledTime}}</p><a href=\"{{MeetingLink}}\">Open</a>",
            Version: 2));

        var message = await Service(source).BuildMeetingInvitationMessageAsync(
            "guest@example.com", "Participant", Link, "Sync", "10:00");

        message.Subject.Should().Be("You're on the list: Sync");
        message.HtmlBody.Should().Contain("<p>Custom CMS body at 10:00</p>");
        message.HtmlBody.Should().Contain($"href=\"{Link}\"");
        message.HtmlBody.Should().NotContain("WarpTalk Meeting Invitation");
        message.TextBody.Should().Contain($"Open ({Link})");
    }

    [Fact]
    public async Task Invitation_UsesAPublishedLayout_OnTheSmtpPathToo()
    {
        var source = new FixedTemplateSource(EmailTemplateCatalog.MeetingInvitation, new StoredEmailTemplate(
            "Invite: {{MeetingTitle}}", "", "<a href=\"{{MeetingLink}}\">Join</a>", 4)
        {
            LayoutHtml = "<html><head></head><body><div class=\"cms-layout\">{{content}}</div></body></html>",
            Preheader = "{{ScheduledTime}}",
        });

        var message = await Service(source).BuildMeetingInvitationMessageAsync("guest@example.com", "Participant", Link, "Sync", "10:00");

        message.HtmlBody.Should().Contain("<div class=\"cms-layout\">");
        message.HtmlBody.Should().NotContain("WarpTalk<span");
    }

    private sealed class FixedTemplateSource(string key, StoredEmailTemplate template) : IEmailTemplateSource
    {
        public Task<StoredEmailTemplate?> FindActiveAsync(string templateKey, string? locale, CancellationToken ct = default) =>
            Task.FromResult(templateKey == key ? template : null);
    }
}
