using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Services;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure.Email;

/// <summary>
/// The meeting title is typed by the host and mailed to everyone they invite. It used to be
/// interpolated into the HTML raw, so a title could put its own links in somebody else's inbox
/// under WarpTalk's name. Every other sender (auth, workspace templates) already encoded.
/// </summary>
public class SmtpEmailServiceEncodingTests
{
    private const string HostileTitle = "<a href=\"https://evil\">x</a>";
    private const string Link = "https://app.warptalk.test/room/5f0c1e9a-7a2b-4c1e-9a3f-2b1d0c9e8f7a";

    private readonly SmtpEmailService _service = new(
        Options.Create(new SmtpSettings { FromEmail = "noreply@warptalk.test", FromName = "WarpTalk" }),
        NullLogger<SmtpEmailService>.Instance);

    [Fact]
    public void Invitation_EncodesHostileTitle()
    {
        var html = _service.BuildMeetingInvitationMessage("guest@example.com", "Participant", Link, HostileTitle, "2026-09-16 10:00").HtmlBody;

        html.Should().Contain("&lt;a href=&quot;https://evil&quot;&gt;x&lt;/a&gt;");
        html.Should().NotContain("https://evil\"");
        html.Should().NotContain("<a href=\"https://evil");
    }

    [Fact]
    public void Invitation_EncodesNameAndTime()
    {
        var html = _service.BuildMeetingInvitationMessage(
            "guest@example.com", "<img src=x onerror=alert(1)>", Link, "Sync", "<b>tomorrow</b>").HtmlBody;

        html.Should().Contain("&lt;img src=x onerror=alert(1)&gt;");
        html.Should().Contain("&lt;b&gt;tomorrow&lt;/b&gt;");
        html.Should().NotContain("<img");
        html.Should().NotContain("<b>tomorrow");
    }

    [Fact]
    public void Reminder_EncodesHostileTitleNameAndStartsIn()
    {
        var html = _service.BuildMeetingReminderMessage(
            "guest@example.com", "<i>Eve</i>", Link, HostileTitle, "<u>5 minutes</u>").HtmlBody;

        html.Should().Contain("&lt;a href=&quot;https://evil&quot;&gt;x&lt;/a&gt;");
        html.Should().Contain("&lt;i&gt;Eve&lt;/i&gt;");
        html.Should().Contain("&lt;u&gt;5 minutes&lt;/u&gt;");
        html.Should().NotContain("<a href=\"https://evil");
    }

    [Fact]
    public void Subject_KeepsTitleAsPlainText()
    {
        // A header is not HTML: encoding it would show recipients "&lt;a href...".
        _service.BuildMeetingInvitationMessage("guest@example.com", "Participant", Link, HostileTitle, "soon")
            .Subject.Should().Be($"Invitation to Meeting: {HostileTitle}");
        _service.BuildMeetingReminderMessage("guest@example.com", "Participant", Link, HostileTitle, "5 minutes")
            .Subject.Should().Be($"Reminder: Meeting '{HostileTitle}' starts in 5 minutes");
    }

    [Fact]
    public void Subject_CannotInjectHeaders()
    {
        var message = _service.BuildMeetingInvitationMessage(
            "guest@example.com", "Participant", Link, "Sync\r\nBcc: attacker@evil.test", "soon");

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        var reparsed = MimeMessage.Load(stream);

        reparsed.Bcc.Count.Should().Be(0);
        reparsed.Headers.Contains(HeaderId.Bcc).Should().BeFalse();
    }

    [Fact]
    public void Link_IsAttributeEncoded()
    {
        var html = _service.BuildMeetingInvitationMessage(
            "guest@example.com", "Participant", "https://app.warptalk.test/room/1?a=1&b='x'", "Sync", "soon").HtmlBody;

        html.Should().Contain("href='https://app.warptalk.test/room/1?a=1&amp;b=&#39;x&#39;'");
        html.Should().NotContain("b='x'");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("/room/123")]
    [InlineData("")]
    public void NonWebLink_IsRefused(string link)
    {
        var invitation = () => _service.BuildMeetingInvitationMessage("guest@example.com", "Participant", link, "Sync", "soon");
        var reminder = () => _service.BuildMeetingReminderMessage("guest@example.com", "Participant", link, "Sync", "5 minutes");

        invitation.Should().Throw<ArgumentException>();
        reminder.Should().Throw<ArgumentException>();
    }
}
