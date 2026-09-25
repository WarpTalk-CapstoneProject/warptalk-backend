using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Services.EmailCms;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>
/// Email content: every transactional email, per locale, with drafts that only publishing sends.
/// Under /admin/notifications so it rides the gateway route the portal already has for this service
/// (AdminRouteExposureTests) instead of widening the admin surface.
/// </summary>
[ApiController]
[Route("api/v1/admin/notifications/email-templates")]
[RequirePermission(AdminPermissions.ContentEmailTemplates)]
public sealed class AdminEmailTemplatesController : CmsControllerBase
{
    private readonly IEmailContentService _emails;

    public AdminEmailTemplatesController(IEmailContentService emails)
    {
        _emails = emails;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => From(await _emails.ListAsync(ct));

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct) => From(await _emails.GetAsync(key, ct));

    [HttpGet("{key}/stats")]
    public async Task<IActionResult> Stats(string key, [FromQuery] int days = 30, CancellationToken ct = default) =>
        From(await _emails.GetStatsAsync(key, days, ct));

    [AdminAudited(AdminAuditCmsActions.EmailBulkAction, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant))]
    [HttpPost("bulk")]
    public Task<IActionResult> Bulk([FromBody] EmailBulkRequest request, CancellationToken ct) =>
        AsActor(actor => _emails.BulkAsync(actor, request, ct));

    // ── One locale ────────────────────────────────────────────────────────────────────────

    /// <summary>Saves the draft. Nothing is sent from it until it is published.</summary>
    [AdminAudited(AdminAuditCmsActions.EmailDraftSaved, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant), EntityRouteKey = "key")]
    [HttpPut("{key}/locales/{locale}/draft")]
    public Task<IActionResult> SaveDraft(string key, string locale, [FromBody] SaveEmailDraftRequest request, CancellationToken ct) =>
        AsActor(actor => _emails.SaveDraftAsync(actor, key, locale, request, ct));

    [AdminAudited(AdminAuditCmsActions.EmailPublished, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant), EntityRouteKey = "key")]
    [HttpPost("{key}/locales/{locale}/publish")]
    public Task<IActionResult> Publish(string key, string locale, [FromBody] PublishEmailRequest request, CancellationToken ct) =>
        AsActor(actor => _emails.PublishAsync(actor, key, locale, request, ct));

    [AdminAudited(AdminAuditCmsActions.EmailDraftDiscarded, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant), EntityRouteKey = "key")]
    [HttpPost("{key}/locales/{locale}/discard-draft")]
    public Task<IActionResult> DiscardDraft(string key, string locale, CancellationToken ct) =>
        AsActor(actor => _emails.DiscardDraftAsync(actor, key, locale, ct));

    [AdminAudited(AdminAuditCmsActions.EmailArchived, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant), EntityRouteKey = "key")]
    [HttpPost("{key}/locales/{locale}/archive")]
    public Task<IActionResult> Archive(string key, string locale, CancellationToken ct) =>
        AsActor(actor => _emails.ArchiveAsync(actor, key, locale, ct));

    [AdminAudited(AdminAuditCmsActions.EmailUnarchived, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant), EntityRouteKey = "key")]
    [HttpPost("{key}/locales/{locale}/unarchive")]
    public Task<IActionResult> Unarchive(string key, string locale, CancellationToken ct) =>
        AsActor(actor => _emails.UnarchiveAsync(actor, key, locale, ct));

    [AdminAudited(AdminAuditCmsActions.EmailDuplicated, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant), EntityRouteKey = "key")]
    [HttpPost("{key}/locales/{locale}/duplicate")]
    public Task<IActionResult> Duplicate(string key, string locale, [FromBody] DuplicateEmailVariantRequest request, CancellationToken ct) =>
        AsActor(actor => _emails.DuplicateAsync(actor, key, locale, request, ct));

    /// <summary>Puts the built-in wording into the draft; it is sent once published.</summary>
    [AdminAudited(AdminAuditCmsActions.EmailResetToDefault, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant), EntityRouteKey = "key")]
    [HttpPost("{key}/locales/{locale}/reset-to-default")]
    public Task<IActionResult> ResetToDefault(string key, string locale, CancellationToken ct) =>
        AsActor(actor => _emails.ResetToDefaultAsync(actor, key, locale, ct));

    [HttpGet("{key}/locales/{locale}/versions")]
    public async Task<IActionResult> Versions(string key, string locale, CancellationToken ct) =>
        From(await _emails.ListVersionsAsync(key, locale, ct));

    /// <summary>Copies a published version back into the draft.</summary>
    [AdminAudited(AdminAuditCmsActions.EmailVersionRestored, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant), EntityRouteKey = "key")]
    [HttpPost("{key}/locales/{locale}/versions/{version:int}/restore")]
    public Task<IActionResult> Restore(string key, string locale, int version, CancellationToken ct) =>
        AsActor(actor => _emails.RestoreVersionAsync(actor, key, locale, version, ct));

    // ── Preview, test, sample data ───────────────────────────────────────────────────────

    [HttpPost("{key}/preview")]
    public async Task<IActionResult> Preview(string key, [FromBody] EmailPreviewRequest request, CancellationToken ct) =>
        From(await _emails.PreviewAsync(key, request, ct));

    /// <summary>Sends an unsaved draft, filled with sample values, to up to five addresses.</summary>
    [AdminAudited(AdminAuditCmsActions.EmailTestSent, AdminAuditEntityTypes.EmailTemplate, typeof(EmailContentVariant), EntityRouteKey = "key")]
    [HttpPost("{key}/test")]
    public Task<IActionResult> SendTest(string key, [FromBody] EmailTestSendRequest request, CancellationToken ct) =>
        AsActor(actor => _emails.SendTestAsync(actor, key, request, ct));

    [AdminAudited(AdminAuditCmsActions.EmailSampleSaved, AdminAuditEntityTypes.EmailSampleData, typeof(EmailSampleDataSet))]
    [HttpPost("{key}/sample-sets")]
    public Task<IActionResult> CreateSampleSet(string key, [FromBody] SaveSampleDataSetRequest request, CancellationToken ct) =>
        AsActor(actor => _emails.SaveSampleSetAsync(actor, key, null, request, ct));

    [AdminAudited(AdminAuditCmsActions.EmailSampleSaved, AdminAuditEntityTypes.EmailSampleData, typeof(EmailSampleDataSet), EntityRouteKey = "id")]
    [HttpPut("{key}/sample-sets/{id:guid}")]
    public Task<IActionResult> UpdateSampleSet(string key, Guid id, [FromBody] SaveSampleDataSetRequest request, CancellationToken ct) =>
        AsActor(actor => _emails.SaveSampleSetAsync(actor, key, id, request, ct));

    [AdminAudited(AdminAuditCmsActions.EmailSampleDeleted, AdminAuditEntityTypes.EmailSampleData, typeof(EmailSampleDataSet), EntityRouteKey = "id")]
    [HttpDelete("{key}/sample-sets/{id:guid}")]
    public Task<IActionResult> DeleteSampleSet(string key, Guid id, CancellationToken ct) =>
        AsActor(actor => _emails.DeleteSampleSetAsync(actor, key, id, ct));
}
