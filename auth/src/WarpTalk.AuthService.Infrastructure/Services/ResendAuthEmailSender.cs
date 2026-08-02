using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Interfaces;

namespace WarpTalk.AuthService.Infrastructure.Services;

public sealed class ResendAuthEmailSender : IAuthEmailSender
{
    private readonly IResendEmailClient _resend;
    private readonly ResendSettings _settings;
    private readonly string _appBaseUrl;

    public ResendAuthEmailSender(
        IResendEmailClient resend,
        IOptions<ResendSettings> settings,
        IConfiguration configuration)
    {
        _resend = resend;
        _settings = settings.Value;
        _appBaseUrl = (configuration["AppBaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
    }

    public Task SendVerificationEmailAsync(
        User user,
        string token,
        CancellationToken ct = default)
    {
        var verifyUrl = $"{_appBaseUrl}/verify-email?token={Uri.EscapeDataString(token)}";
        var name = System.Net.WebUtility.HtmlEncode(user.FullName);
        var html = BuildAnthropicEmailLayout(
            "Verify your email address",
            $"<p style=\"margin: 0 0 16px 0; font-size: 15px; line-height: 1.6; color: #3F3F46;\">Hi <strong style=\"color: #18181B;\">{name}</strong>,</p>" +
            "<p style=\"margin: 0 0 28px 0; font-size: 15px; line-height: 1.6; color: #3F3F46;\">Welcome to WarpTalk! Please verify your email address by clicking the button below to complete your registration.</p>" +
            $"<div style=\"margin: 32px 0;\"><a href=\"{verifyUrl}\" target=\"_blank\" style=\"display: inline-block; background-color: #18181B; color: #FFFFFF; font-size: 14px; font-weight: 600; text-decoration: none; padding: 14px 28px; border-radius: 10px; box-shadow: 0 2px 6px rgba(0, 0, 0, 0.08);\">Verify Email Address &rarr;</a></div>" +
            $"<div style=\"background-color: #FAFAFA; border: 1px solid #F4F4F5; border-radius: 10px; padding: 16px; margin: 24px 0;\"><p style=\"margin: 0 0 6px 0; font-size: 12px; font-weight: 500; color: #71717A;\">Or copy and paste this link into your browser:</p><a href=\"{verifyUrl}\" target=\"_blank\" style=\"font-size: 13px; color: #D97757; text-decoration: none; word-break: break-all;\">{verifyUrl}</a></div>");

        return SendAsync(
            user.Email,
            "Verify your WarpTalk email",
            html,
            $"Verify your WarpTalk email: {verifyUrl}",
            ct);
    }

    public Task SendPasswordResetEmailAsync(
        User user,
        string token,
        CancellationToken ct = default)
    {
        var resetUrl = $"{_appBaseUrl}/reset-password?token={Uri.EscapeDataString(token)}";
        var name = System.Net.WebUtility.HtmlEncode(user.FullName);
        var html = BuildAnthropicEmailLayout(
            "Reset your password",
            $"<p style=\"margin: 0 0 16px 0; font-size: 15px; line-height: 1.6; color: #3F3F46;\">Hi <strong style=\"color: #18181B;\">{name}</strong>,</p>" +
            "<p style=\"margin: 0 0 28px 0; font-size: 15px; line-height: 1.6; color: #3F3F46;\">We received a request to reset your WarpTalk account password. Click the button below to choose a new password.</p>" +
            $"<div style=\"margin: 32px 0;\"><a href=\"{resetUrl}\" target=\"_blank\" style=\"display: inline-block; background-color: #18181B; color: #FFFFFF; font-size: 14px; font-weight: 600; text-decoration: none; padding: 14px 28px; border-radius: 10px; box-shadow: 0 2px 6px rgba(0, 0, 0, 0.08);\">Reset Password &rarr;</a></div>" +
            $"<div style=\"background-color: #FAFAFA; border: 1px solid #F4F4F5; border-radius: 10px; padding: 16px; margin: 24px 0;\"><p style=\"margin: 0 0 6px 0; font-size: 12px; font-weight: 500; color: #71717A;\">Or copy and paste this link into your browser:</p><a href=\"{resetUrl}\" target=\"_blank\" style=\"font-size: 13px; color: #D97757; text-decoration: none; word-break: break-all;\">{resetUrl}</a></div>" +
            "<p style=\"margin: 0; font-size: 13px; color: #71717A;\">This link expires soon and can only be used once.</p>");

        return SendAsync(
            user.Email,
            "Reset your WarpTalk password",
            html,
            $"Reset your WarpTalk password: {resetUrl}",
            ct);
    }

    private static string BuildAnthropicEmailLayout(string title, string bodyContent)
    {
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{title}</title>
</head>
<body style=""margin: 0; padding: 0; width: 100%; background-color: #FBF9F5; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;"">
    <table role=""presentation"" width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"" style=""background-color: #FBF9F5; padding: 48px 16px;"">
        <tr>
            <td align=""center"">
                <table role=""presentation"" width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"" style=""max-width: 540px; background-color: #FFFFFF; border: 1px solid #E4E4E7; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 20px rgba(0, 0, 0, 0.03);"">
                    <tr>
                        <td style=""padding: 40px 36px 0 36px;"">
                            <div style=""font-size: 22px; font-weight: 700; color: #18181B; letter-spacing: -0.5px;"">
                                WarpTalk<span style=""color: #D97757; font-weight: 900;"">.</span>
                            </div>
                        </td>
                    </tr>
                    <tr>
                        <td style=""padding: 24px 36px 40px 36px;"">
                            <h1 style=""margin: 0 0 20px 0; font-size: 22px; font-weight: 700; color: #18181B; line-height: 1.3; letter-spacing: -0.3px;"">{title}</h1>
                            {bodyContent}
                            <hr style=""border: 0; border-top: 1px solid #F4F4F5; margin: 32px 0 24px 0;"" />
                            <p style=""margin: 0; font-size: 12px; line-height: 1.6; color: #A1A1AA;"">
                                This email was sent to you by WarpTalk. If you did not initiate this request, please contact support.
                            </p>
                        </td>
                    </tr>
                </table>
                <table role=""presentation"" width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"" style=""max-width: 540px; margin-top: 24px;"">
                    <tr>
                        <td align=""center"" style=""font-size: 12px; color: #A1A1AA;"">
                            &copy; 2026 WarpTalk Inc. &bull; Real-time AI Workspace Collaboration
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
    }

    private async Task SendAsync(
        string to,
        string subject,
        string html,
        string text,
        CancellationToken ct)
    {
        var result = await _resend.SendEmailAsync(
            new SendEmailRequest(
                $"{_settings.FromName} <{_settings.FromEmail}>",
                to,
                subject,
                html,
                text),
            ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.ErrorMessage ?? "Email provider rejected the message.");
    }
}
