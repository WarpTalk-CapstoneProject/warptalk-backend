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
        => SendAsync(
            user.Email,
            "Verify your WarpTalk email",
            $"<p>Hi {System.Net.WebUtility.HtmlEncode(user.FullName)},</p>" +
            $"<p><a href=\"{_appBaseUrl}/verify-email?token={Uri.EscapeDataString(token)}\">Verify your email</a></p>",
            $"Verify your WarpTalk email: {_appBaseUrl}/verify-email?token={Uri.EscapeDataString(token)}",
            ct);

    public Task SendPasswordResetEmailAsync(
        User user,
        string token,
        CancellationToken ct = default)
        => SendAsync(
            user.Email,
            "Reset your WarpTalk password",
            $"<p>Hi {System.Net.WebUtility.HtmlEncode(user.FullName)},</p>" +
            $"<p><a href=\"{_appBaseUrl}/reset-password?token={Uri.EscapeDataString(token)}\">Reset your password</a></p>" +
            "<p>This link expires soon and can only be used once.</p>",
            $"Reset your WarpTalk password: {_appBaseUrl}/reset-password?token={Uri.EscapeDataString(token)}",
            ct);

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
