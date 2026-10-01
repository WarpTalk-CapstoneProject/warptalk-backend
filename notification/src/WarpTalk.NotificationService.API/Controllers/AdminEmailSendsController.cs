using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Services.EmailCms;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>
/// Audience sends of custom email templates: estimate who it reaches, start (the admin confirms the
/// number), follow each recipient's outcome, cancel. Its own permission, content.email_send —
/// editing a template reaches nobody, this reaches every account in the audience.
/// </summary>
[ApiController]
[Route("api/v1/admin/notifications/email-sends")]
[RequirePermission(AdminPermissions.ContentEmailSend)]
public sealed class AdminEmailSendsController : CmsControllerBase
{
    private readonly IEmailCampaignService _sends;

    public AdminEmailSendsController(IEmailCampaignService sends)
    {
        _sends = sends;
    }

    /// <summary>How many people the audience reaches now, by language. Sends nothing.</summary>
    [HttpPost("templates/{key}/estimate")]
    public async Task<IActionResult> Estimate(string key, [FromBody] EmailSendEstimateRequest request, CancellationToken ct) =>
        From(await _sends.EstimateAsync(key, request, ct));

    [AdminAudited(AdminAuditCmsActions.EmailSendStarted, AdminAuditEntityTypes.EmailCampaign, typeof(EmailCampaign))]
    [HttpPost("templates/{key}")]
    public Task<IActionResult> Start(string key, [FromBody] CreateEmailSendRequest request, CancellationToken ct) =>
        AsActor(actor => _sends.CreateAsync(actor, key, request, ct));

    [HttpGet("templates/{key}")]
    public async Task<IActionResult> List(string key, CancellationToken ct) => From(await _sends.ListAsync(key, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => From(await _sends.GetAsync(id, ct));

    [HttpGet("{id:guid}/recipients")]
    public async Task<IActionResult> Recipients(
        Guid id, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        From(await _sends.RecipientsAsync(id, status, page, pageSize, ct));

    [AdminAudited(AdminAuditCmsActions.EmailSendCancelled, AdminAuditEntityTypes.EmailCampaign, typeof(EmailCampaign), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        AsActor(actor => _sends.CancelAsync(actor, id, ct));
}
