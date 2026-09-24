using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Email;
using WarpTalk.Shared.Interfaces;

namespace WarpTalk.Shared.Services;

/// <summary>
/// The meeting emails, sent over SMTP. Their wording is an admin-editable template
/// (<see cref="EmailTemplateCatalog.MeetingInvitation"/>, <see cref="EmailTemplateCatalog.MeetingReminder"/>)
/// read through <see cref="IEmailTemplateComposer"/> on every send, falling back to the built-in
/// wording when nobody has edited it.
/// </summary>
public class SmtpEmailService : IEmailService
{
    private readonly SmtpSettings _settings;
    private readonly IEmailTemplateComposer _templates;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<SmtpSettings> options, IEmailTemplateComposer templates, ILogger<SmtpEmailService> logger)
    {
        _settings = options.Value;
        _templates = templates;
        _logger = logger;
    }

    public async Task SendMeetingInvitationAsync(string toEmail, string participantName, string meetingLink, string meetingTitle, string scheduledTime, CancellationToken ct = default)
    {
        try
        {
            var message = await BuildMeetingInvitationMessageAsync(toEmail, participantName, meetingLink, meetingTitle, scheduledTime, ct);
            await SendEmailAsync(message, ct);
            _logger.LogInformation("Invitation email sent successfully to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send invitation email to {Email}", toEmail);
        }
    }

    public async Task SendMeetingReminderAsync(string toEmail, string participantName, string meetingLink, string meetingTitle, string startsIn, CancellationToken ct = default)
    {
        try
        {
            var message = await BuildMeetingReminderMessageAsync(toEmail, participantName, meetingLink, meetingTitle, startsIn, ct);
            await SendEmailAsync(message, ct);
            _logger.LogInformation("Reminder email sent successfully to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send reminder email to {Email}", toEmail);
        }
    }

    // The meeting title is chosen by the host and lands in other people's inboxes, so every value
    // substituted into the HTML is encoded (EmailTemplateRenderer does that for every template).
    // The subject is a MIME header, not HTML: MimeKit encodes it, and encoding it here would show
    // recipients a literal "&lt;".
    public async Task<MimeMessage> BuildMeetingInvitationMessageAsync(string toEmail, string participantName, string meetingLink, string meetingTitle, string scheduledTime, CancellationToken ct = default)
    {
        var link = RequireWebLink(meetingLink);
        var email = await _templates.ComposeAsync(
            EmailTemplateCatalog.MeetingInvitation,
            new Dictionary<string, string>
            {
                ["ParticipantName"] = participantName,
                ["MeetingTitle"] = meetingTitle,
                ["ScheduledTime"] = scheduledTime,
                ["MeetingLink"] = link,
            },
            ct);
        return ToMimeMessage(toEmail, participantName, email);
    }

    public async Task<MimeMessage> BuildMeetingReminderMessageAsync(string toEmail, string participantName, string meetingLink, string meetingTitle, string startsIn, CancellationToken ct = default)
    {
        var link = RequireWebLink(meetingLink);
        var email = await _templates.ComposeAsync(
            EmailTemplateCatalog.MeetingReminder,
            new Dictionary<string, string>
            {
                ["ParticipantName"] = participantName,
                ["MeetingTitle"] = meetingTitle,
                ["StartsIn"] = startsIn,
                ["MeetingLink"] = link,
            },
            ct);
        return ToMimeMessage(toEmail, participantName, email);
    }

    private MimeMessage ToMimeMessage(string toEmail, string participantName, RenderedEmail email)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
        message.To.Add(new MailboxAddress(participantName, toEmail));
        message.Subject = email.Subject;
        message.Body = new BodyBuilder { HtmlBody = email.HtmlBody, TextBody = email.TextBody }.ToMessageBody();
        return message;
    }

    // A join button must point at a web page. Anything else (javascript:, mailto:, a relative path
    // from a missing FrontendBaseUrl) is refused rather than mailed, and the caller logs the failure.
    private static string RequireWebLink(string meetingLink)
    {
        if (!Uri.TryCreate(meetingLink, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Meeting link must be an absolute http(s) URL.", nameof(meetingLink));
        }

        return uri.AbsoluteUri;
    }

    private async Task SendEmailAsync(MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient();

        // Accept all SSL certificates (in case the server supports STARTTLS)
        client.ServerCertificateValidationCallback = (s, c, h, e) => true;

        await client.ConnectAsync(_settings.Host, _settings.Port, SecureSocketOptions.Auto, ct);

        if (!string.IsNullOrEmpty(_settings.Username))
        {
            await client.AuthenticateAsync(_settings.Username, _settings.Password, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
