using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Resend;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.NotificationService.Infrastructure.Services;

public class ResendEmailSender : IEmailSender
{
    private readonly IResend _resend;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResendEmailSender> _logger;
    private readonly IPlatformSettings? _platformSettings;

    public ResendEmailSender(
        IResend resend,
        IConfiguration configuration,
        ILogger<ResendEmailSender> logger,
        IPlatformSettings? platformSettings = null)
    {
        _resend = resend;
        _configuration = configuration;
        _logger = logger;
        _platformSettings = platformSettings;
    }

    /// <summary>
    /// The message handed to Resend. Sender and reply-to come from /admin/settings
    /// (notifications.email.*) per message, falling back to Resend:FromEmail; an explicit sender on
    /// the message still wins.
    /// </summary>
    public async Task<Resend.EmailMessage> ComposeAsync(WarpTalk.NotificationService.Application.Interfaces.EmailMessage message, CancellationToken ct = default)
    {
        var (configuredName, configuredAddress) = EmailSenderSettings.Parse(
            _configuration["Resend:FromEmail"] ?? "WarpTalk <onboarding@resend.dev>");
        var sender = await EmailSenderSettings.ResolveAsync(_platformSettings, configuredName, configuredAddress, ct);

        var resendMessage = new Resend.EmailMessage
        {
            From = !string.IsNullOrWhiteSpace(message.FromEmail) ? message.FromEmail : sender.From,
            Subject = message.Subject,
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody
        };
        resendMessage.To.Add(message.ToEmail);
        if (sender.ReplyTo is { } replyTo)
        {
            resendMessage.ReplyTo = replyTo;
        }

        return resendMessage;
    }

    public async Task<bool> SendEmailAsync(WarpTalk.NotificationService.Application.Interfaces.EmailMessage message, CancellationToken ct = default)
    {
        try
        {
            var resendMessage = await ComposeAsync(message, ct);

            var response = await _resend.EmailSendAsync(resendMessage, ct);
            _logger.LogInformation("Successfully sent email via official Resend SDK to {ToEmail} (Id: {ResendId})", message.ToEmail, response.Content);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email via official Resend SDK to {ToEmail}", message.ToEmail);
            return false;
        }
    }
}
