namespace WarpTalk.NotificationService.Application.Interfaces;

public record EmailMessage(
    string ToEmail,
    string Subject,
    string HtmlBody,
    string? ToName = null,
    string? TextBody = null,
    string? FromEmail = null,
    string? FromName = null
);

public interface IEmailSender
{
    Task<bool> SendEmailAsync(EmailMessage message, CancellationToken ct = default);
}
