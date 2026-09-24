using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Email;
using WarpTalk.Shared.Interfaces;
using WarpTalk.Shared.Models;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Adapters;

/// <summary>
/// The two workspace emails. Their wording is an admin-editable template
/// (<see cref="EmailTemplateCatalog.WorkspaceInvitation"/>,
/// <see cref="EmailTemplateCatalog.WorkspaceJoinRequestApproved"/>) read through
/// <see cref="IEmailTemplateComposer"/> on every send, falling back to the built-in wording.
/// </summary>
public class WorkspaceInvitationEmailComposer : IWorkspaceInvitationEmailComposer
{
    private readonly IResendEmailClient _resendClient;
    private readonly IEmailTemplateComposer _templates;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceInvitationEmailComposer> _logger;

    public WorkspaceInvitationEmailComposer(
        IResendEmailClient resendClient,
        IEmailTemplateComposer templates,
        IConfiguration configuration,
        ILogger<WorkspaceInvitationEmailComposer> logger)
    {
        _resendClient = resendClient;
        _templates = templates;
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
        var appBaseUrl = AppBaseUrl();
        var joinUrl = $"{appBaseUrl}/login?token={Uri.EscapeDataString(invitationToken)}";

        var email = await _templates.ComposeAsync(
            EmailTemplateCatalog.WorkspaceInvitation,
            new Dictionary<string, string>
            {
                ["WorkspaceName"] = workspace.Name,
                ["InviterName"] = inviterName,
                ["RoleName"] = roleName,
                ["JoinUrl"] = joinUrl,
                ["AppBaseUrl"] = appBaseUrl,
            },
            ct);

        _logger.LogInformation("Dispatching invitation email to {Email} for workspace {WorkspaceName} via Resend", invitation.Email, workspace.Name);
        return await _resendClient.SendEmailAsync(
            new SendEmailRequest(From(), invitation.Email, email.Subject, email.HtmlBody, email.TextBody),
            ct);
    }

    public async Task<SendEmailResponse> SendJoinRequestApprovedEmailAsync(
        WorkspaceInvitation invitation,
        Workspace workspace,
        CancellationToken ct = default)
    {
        var appBaseUrl = AppBaseUrl();
        var joinUrl = $"{appBaseUrl}/{workspace.Slug}/home";

        var email = await _templates.ComposeAsync(
            EmailTemplateCatalog.WorkspaceJoinRequestApproved,
            new Dictionary<string, string>
            {
                ["WorkspaceName"] = workspace.Name,
                ["MembershipType"] = invitation.MembershipType ?? string.Empty,
                ["JoinUrl"] = joinUrl,
                ["AppBaseUrl"] = appBaseUrl,
            },
            ct);

        _logger.LogInformation("Dispatching join request approval email to {Email} for workspace {WorkspaceName} via Resend", invitation.Email, workspace.Name);
        return await _resendClient.SendEmailAsync(
            new SendEmailRequest(From(), invitation.Email, email.Subject, email.HtmlBody, email.TextBody),
            ct);
    }

    private string AppBaseUrl() => _configuration["AppBaseUrl"]?.TrimEnd('/') ?? "http://localhost:3000";

    private string From()
    {
        var fromEmail = _configuration["Resend:FromEmail"] ?? "no-reply@warptalk.vn";
        var fromName = _configuration["Resend:FromName"] ?? "WarpTalk";
        return $"{fromName} <{fromEmail}>";
    }
}
