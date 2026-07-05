using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Interfaces;

namespace WarpTalk.Shared.Services;

public class SmtpEmailService : IEmailService
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<SmtpSettings> options, ILogger<SmtpEmailService> logger)
    {
        _settings = options.Value;
        _logger = logger;
    }

    public async Task SendMeetingInvitationAsync(string toEmail, string participantName, string meetingLink, string meetingTitle, string scheduledTime, CancellationToken ct = default)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
            message.To.Add(new MailboxAddress(participantName, toEmail));
            message.Subject = $"Invitation to Meeting: {meetingTitle}";

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = $@"
                    <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; border: 1px solid #eee; border-radius: 8px;'>
                        <h2 style='color: #4F46E5;'>WarpTalk Meeting Invitation</h2>
                        <p>Hello <strong>{participantName}</strong>,</p>
                        <p>You have been invited to a meeting:</p>
                        <ul style='list-style-type: none; padding: 0;'>
                            <li><strong>Title:</strong> {meetingTitle}</li>
                            <li><strong>Scheduled Time:</strong> {scheduledTime}</li>
                        </ul>
                        <p style='margin-top: 30px;'>
                            <a href='{meetingLink}' style='background-color: #4F46E5; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; display: inline-block; font-weight: bold;'>Join Meeting</a>
                        </p>
                        <p style='margin-top: 30px; font-size: 12px; color: #666;'>
                            If the button doesn't work, copy and paste this link into your browser: <br/>
                            <a href='{meetingLink}'>{meetingLink}</a>
                        </p>
                    </div>"
            };

            message.Body = bodyBuilder.ToMessageBody();
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
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
            message.To.Add(new MailboxAddress(participantName, toEmail));
            message.Subject = $"Reminder: Meeting '{meetingTitle}' starts in {startsIn}";

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = $@"
                    <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; border: 1px solid #eee; border-radius: 8px;'>
                        <h2 style='color: #E11D48;'>Meeting Reminder</h2>
                        <p>Hello <strong>{participantName}</strong>,</p>
                        <p>This is a reminder that your meeting is starting soon:</p>
                        <ul style='list-style-type: none; padding: 0;'>
                            <li><strong>Title:</strong> {meetingTitle}</li>
                            <li><strong>Starts In:</strong> {startsIn}</li>
                        </ul>
                        <p style='margin-top: 30px;'>
                            <a href='{meetingLink}' style='background-color: #E11D48; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; display: inline-block; font-weight: bold;'>Join Meeting Now</a>
                        </p>
                    </div>"
            };

            message.Body = bodyBuilder.ToMessageBody();
            await SendEmailAsync(message, ct);
            _logger.LogInformation("Reminder email sent successfully to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send reminder email to {Email}", toEmail);
        }
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
