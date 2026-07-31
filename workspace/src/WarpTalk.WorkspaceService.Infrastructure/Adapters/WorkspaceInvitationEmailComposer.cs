using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Interfaces;
using WarpTalk.Shared.Models;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Adapters;

public class WorkspaceInvitationEmailComposer : IWorkspaceInvitationEmailComposer
{
    private readonly IResendEmailClient _resendClient;
    private readonly IEmailTemplateProvider _templateProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceInvitationEmailComposer> _logger;

    public WorkspaceInvitationEmailComposer(
        IResendEmailClient resendClient,
        IEmailTemplateProvider templateProvider,
        IConfiguration configuration,
        ILogger<WorkspaceInvitationEmailComposer> logger)
    {
        _resendClient = resendClient;
        _templateProvider = templateProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SendEmailResponse> SendInvitationEmailAsync(
        WorkspaceInvitation invitation,
        Workspace workspace,
        string inviterName,
        string roleName,
        CancellationToken ct = default)
    {
        var appBaseUrl = _configuration["AppBaseUrl"]?.TrimEnd('/') ?? "http://localhost:3000";
        var fromEmail = _configuration["Resend:FromEmail"] ?? "no-reply@warptalk.vn";
        var fromName = _configuration["Resend:FromName"] ?? "WarpTalk";
        var from = $"{fromName} <{fromEmail}>";

        var joinUrl = $"{appBaseUrl}/{workspace.Slug}/home";
        var subject = $"You've been invited to join {workspace.Name} on WarpTalk";

        var htmlTemplate = await _templateProvider.GetTemplateAsync("workspace-invitation-email", ct);

        var htmlBody = htmlTemplate
            .Replace("{{WorkspaceName}}", System.Net.WebUtility.HtmlEncode(workspace.Name))
            .Replace("{{InviterName}}", System.Net.WebUtility.HtmlEncode(inviterName))
            .Replace("{{RoleName}}", System.Net.WebUtility.HtmlEncode(roleName))
            .Replace("{{JoinUrl}}", joinUrl)
            .Replace("{{AppBaseUrl}}", appBaseUrl);

        var textBody = $"{inviterName} has invited you to join the {workspace.Name} workspace as a {roleName}.\n\n" +
                       $"Click here to join: {joinUrl}";

        var request = new SendEmailRequest(
            from,
            invitation.Email,
            subject,
            htmlBody,
            textBody
        );

        _logger.LogInformation("Dispatching invitation email to {Email} for workspace {WorkspaceName} via Resend", invitation.Email, workspace.Name);
        return await _resendClient.SendEmailAsync(request, ct);
    }
<<<<<<< HEAD

    public async Task<SendEmailResponse> SendJoinRequestApprovedEmailAsync(
        WorkspaceInvitation invitation,
        Workspace workspace,
        CancellationToken ct = default)
    {
        var appBaseUrl = _configuration["AppBaseUrl"]?.TrimEnd('/') ?? "http://localhost:3000";
        var fromEmail = _configuration["Resend:FromEmail"] ?? "no-reply@warptalk.vn";
        var fromName = _configuration["Resend:FromName"] ?? "WarpTalk";
        var from = $"{fromName} <{fromEmail}>";
        var joinUrl = $"{appBaseUrl}/{workspace.Slug}/home";
        var subject = $"Your request to join {workspace.Name} was approved";

        var htmlTemplate = await LoadTemplateHtmlAsync(appBaseUrl, ct, "workspace-join-request-approved-email.html");
        var htmlBody = htmlTemplate
            .Replace("{{WorkspaceName}}", System.Net.WebUtility.HtmlEncode(workspace.Name))
            .Replace("{{MembershipType}}", System.Net.WebUtility.HtmlEncode(invitation.MembershipType))
            .Replace("{{JoinUrl}}", joinUrl)
            .Replace("{{AppBaseUrl}}", appBaseUrl);
        var textBody = $"Your request to join {workspace.Name} was approved as a Member ({invitation.MembershipType}).\n\n" +
                       $"Open the workspace: {joinUrl}";

        var request = new SendEmailRequest(from, invitation.Email, subject, htmlBody, textBody);
        _logger.LogInformation("Dispatching join request approval email to {Email} for workspace {WorkspaceName} via Resend", invitation.Email, workspace.Name);
        return await _resendClient.SendEmailAsync(request, ct);
    }

    private async Task<string> LoadTemplateHtmlAsync(
        string appBaseUrl,
        CancellationToken ct,
        string templateFileName = "workspace-invitation-email.html")
    {
        try
        {
            // 1. Check local web template file path if running in same monorepo root
            var webTemplatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "warptalk-web", "public", "templates", templateFileName);
            if (File.Exists(webTemplatePath))
            {
                return await File.ReadAllTextAsync(webTemplatePath, ct);
            }

            // 2. Check local fallback template file
            var localTemplatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", templateFileName);
            if (File.Exists(localTemplatePath))
            {
                return await File.ReadAllTextAsync(localTemplatePath, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load external HTML template file. Falling back to default HTML template.");
        }

        if (templateFileName == "workspace-join-request-approved-email.html")
        {
            return @"<!DOCTYPE html>
<html>
<head><meta charset=""utf-8""><style>
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background-color: #0f172a; color: #f8fafc; padding: 32px; }
.container { max-width: 560px; margin: 0 auto; background-color: #1e293b; border-radius: 12px; padding: 32px; }
.header { font-size: 20px; font-weight: 700; margin-bottom: 16px; }
.btn { display: inline-block; background-color: #2563eb; color: #ffffff !important; font-weight: 600; text-decoration: none; padding: 12px 24px; border-radius: 8px; margin-top: 16px; }
</style></head>
<body><div class=""container""><div class=""header"">Your request was approved</div>
<p>Your request to join <strong>{{WorkspaceName}}</strong> was approved as a <strong>Member</strong> with membership type <strong>{{MembershipType}}</strong>.</p>
<a href=""{{JoinUrl}}"" class=""btn"">Open Workspace</a></div></body></html>";
        }

        // Fallback HTML structure for normal invitations
        return @"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background-color: #0f172a; color: #f8fafc; padding: 32px; }
        .container { max-width: 560px; margin: 0 auto; background-color: #1e293b; border-radius: 12px; padding: 32px; }
        .header { font-size: 20px; font-weight: 700; color: #f8fafc; margin-bottom: 16px; }
        .btn { display: inline-block; background-color: #2563eb; color: #ffffff !important; font-weight: 600; text-decoration: none; padding: 12px 24px; border-radius: 8px; margin-top: 16px; }
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">Join {{WorkspaceName}} on WarpTalk</div>
        <p>Hello,</p>
        <p><strong>{{InviterName}}</strong> has invited you to join the <strong>{{WorkspaceName}}</strong> workspace as a <strong>{{RoleName}}</strong>.</p>
        <a href=""{{JoinUrl}}"" class=""btn"">Accept & Join Workspace</a>
    </div>
</body>
</html>";
    }
=======
>>>>>>> feat/configurable-invitation-expiry
}
