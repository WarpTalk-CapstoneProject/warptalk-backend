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
        string invitationToken,
        CancellationToken ct = default)
    {
        var appBaseUrl = _configuration["AppBaseUrl"]?.TrimEnd('/') ?? "http://localhost:3000";
        var fromEmail = _configuration["Resend:FromEmail"] ?? "no-reply@warptalk.vn";
        var fromName = _configuration["Resend:FromName"] ?? "WarpTalk";
        var from = $"{fromName} <{fromEmail}>";

        var joinUrl = $"{appBaseUrl}/invitations/{Uri.EscapeDataString(invitationToken)}";
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
}
