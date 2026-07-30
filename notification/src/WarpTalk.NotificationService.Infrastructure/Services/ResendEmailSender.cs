using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Resend;
using WarpTalk.NotificationService.Application.Interfaces;

namespace WarpTalk.NotificationService.Infrastructure.Services;

public class ResendEmailSender : IEmailSender
{
    private readonly IResend _resend;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(
        IResend resend,
        IConfiguration configuration,
        ILogger<ResendEmailSender> logger)
    {
        _resend = resend;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> SendEmailAsync(WarpTalk.NotificationService.Application.Interfaces.EmailMessage message, CancellationToken ct = default)
    {
        try
        {
            var defaultFrom = _configuration["Resend:FromEmail"] ?? "WarpTalk <onboarding@resend.dev>";
            var fromEmail = !string.IsNullOrWhiteSpace(message.FromEmail) 
                ? message.FromEmail 
                : defaultFrom;

            var resendMessage = new Resend.EmailMessage
            {
                From = fromEmail,
                Subject = message.Subject,
                HtmlBody = message.HtmlBody,
                TextBody = message.TextBody
            };
            resendMessage.To.Add(message.ToEmail);

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
