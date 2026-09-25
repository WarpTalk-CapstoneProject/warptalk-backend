using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.Shared.Configuration;
using WarpTalk.Shared.Email;
using WarpTalk.Shared.Interfaces;

namespace WarpTalk.AuthService.Infrastructure.Services;

/// <summary>
/// Sends the two account emails. Their wording is an admin-editable template
/// (<see cref="EmailTemplateCatalog.AuthVerifyEmail"/>, <see cref="EmailTemplateCatalog.AuthPasswordReset"/>),
/// read through <see cref="IEmailTemplateComposer"/> at every send and falling back to the
/// built-in wording when nobody has edited it.
/// </summary>
public sealed class ResendAuthEmailSender : IAuthEmailSender
{
    private readonly IResendEmailClient _resend;
    private readonly IEmailTemplateComposer _templates;
    private readonly IEmailDeliveryRecorder _deliveries;
    private readonly string _appBaseUrl;

    public ResendAuthEmailSender(
        IResendEmailClient resend,
        IEmailTemplateComposer templates,
        IOptions<ResendSettings> settings,
        IConfiguration configuration,
        IEmailDeliveryRecorder? deliveries = null)
    {
        _resend = resend;
        _templates = templates;
        _deliveries = deliveries ?? NullEmailDeliveryRecorder.Instance;
        // The sender is no longer read here: the Resend client composes it per message from
        // /admin/settings with these same ResendSettings as the fallback. Kept in the signature so
        // the DI registration and its callers are unchanged.
        _ = settings;
        _appBaseUrl = (configuration["AppBaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
    }

    public async Task SendVerificationEmailAsync(
        User user,
        string token,
        CancellationToken ct = default)
    {
        var verifyUrl = $"{_appBaseUrl}/verify-email?token={Uri.EscapeDataString(token)}";
        var email = await _templates.ComposeAsync(
            EmailTemplateCatalog.AuthVerifyEmail,
            new Dictionary<string, string>
            {
                ["FullName"] = user.FullName,
                ["VerifyUrl"] = verifyUrl,
            },
            user.PreferredLanguage,
            ct);

        await SendAsync(user.Email, email, ct);
    }

    public async Task SendPasswordResetEmailAsync(
        User user,
        string token,
        CancellationToken ct = default)
    {
        var resetUrl = $"{_appBaseUrl}/reset-password?token={Uri.EscapeDataString(token)}";
        var email = await _templates.ComposeAsync(
            EmailTemplateCatalog.AuthPasswordReset,
            new Dictionary<string, string>
            {
                ["FullName"] = user.FullName,
                ["ResetUrl"] = resetUrl,
            },
            user.PreferredLanguage,
            ct);

        await SendAsync(user.Email, email, ct);
    }

    public async Task SendStaffInvitationEmailAsync(
        string toEmail,
        string inviterName,
        string roleName,
        CancellationToken ct = default)
    {
        var email = await _templates.ComposeAsync(
            EmailTemplateCatalog.AuthStaffInvitation,
            new Dictionary<string, string>
            {
                ["InviterName"] = inviterName,
                ["RoleName"] = roleName,
                ["SignInUrl"] = $"{_appBaseUrl}/login?redirect={Uri.EscapeDataString("/admin")}",
                ["ExpiresIn"] = $"{StaffConstants.InvitationLifetimeDays} days",
            },
            // The address may have no account yet, so there is no language to honour: the
            // template's default locale.
            locale: null,
            ct);

        await SendAsync(toEmail, email, ct);
    }

    private async Task SendAsync(string to, RenderedEmail email, CancellationToken ct)
    {
        var result = await _resend.SendEmailAsync(
            new SendEmailRequest(
                // Empty: the client composes the sender from /admin/settings
                // (notifications.email.*), falling back to these same Resend settings.
                string.Empty,
                to,
                email.Subject,
                email.HtmlBody,
                email.TextBody),
            ct);
        await _deliveries.RecordAsync(email, result.IsSuccess, ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.ErrorMessage ?? "Email provider rejected the message.");
    }
}
