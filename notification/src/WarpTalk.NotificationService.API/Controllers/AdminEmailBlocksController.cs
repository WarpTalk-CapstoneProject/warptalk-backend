using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Services.EmailCms;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>Email layouts and reusable blocks — the design, managed apart from each email's wording.</summary>
[ApiController]
[Route("api/v1/admin/notifications/email-blocks")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public sealed class AdminEmailBlocksController : CmsControllerBase
{
    private readonly IEmailBlockService _blocks;

    public AdminEmailBlocksController(IEmailBlockService blocks)
    {
        _blocks = blocks;
    }

    /// <summary>kind=LAYOUT or PARTIAL; omitted lists both.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? kind, CancellationToken ct) => From(await _blocks.ListAsync(kind, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => From(await _blocks.GetAsync(id, ct));

    [AdminAudited(AdminAuditCmsActions.EmailBlockCreated, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock))]
    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreateEmailBlockRequest request, CancellationToken ct) =>
        AsActor(actor => _blocks.CreateAsync(actor, request, ct),
            created => Created($"/api/v1/admin/notifications/email-blocks/{created.Id}", created));

    [AdminAudited(AdminAuditCmsActions.EmailBlockDraftSaved, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock), EntityRouteKey = "id")]
    [HttpPut("{id:guid}/draft")]
    public Task<IActionResult> SaveDraft(Guid id, [FromBody] SaveEmailBlockDraftRequest request, CancellationToken ct) =>
        AsActor(actor => _blocks.SaveDraftAsync(actor, id, request, ct));

    [AdminAudited(AdminAuditCmsActions.EmailBlockPublished, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/publish")]
    public Task<IActionResult> Publish(Guid id, [FromBody] PublishEmailRequest request, CancellationToken ct) =>
        AsActor(actor => _blocks.PublishAsync(actor, id, request, ct));

    [AdminAudited(AdminAuditCmsActions.EmailBlockDraftDiscarded, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/discard-draft")]
    public Task<IActionResult> DiscardDraft(Guid id, CancellationToken ct) =>
        AsActor(actor => _blocks.DiscardDraftAsync(actor, id, ct));

    [AdminAudited(AdminAuditCmsActions.EmailBlockDuplicated, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/duplicate")]
    public Task<IActionResult> Duplicate(Guid id, [FromBody] DuplicateEmailBlockRequest request, CancellationToken ct) =>
        AsActor(actor => _blocks.DuplicateAsync(actor, id, request, ct),
            created => Created($"/api/v1/admin/notifications/email-blocks/{created.Id}", created));

    [AdminAudited(AdminAuditCmsActions.EmailBlockArchived, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/archive")]
    public Task<IActionResult> Archive(Guid id, CancellationToken ct) => AsActor(actor => _blocks.ArchiveAsync(actor, id, ct));

    [AdminAudited(AdminAuditCmsActions.EmailBlockUnarchived, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/unarchive")]
    public Task<IActionResult> Unarchive(Guid id, CancellationToken ct) => AsActor(actor => _blocks.UnarchiveAsync(actor, id, ct));

    [AdminAudited(AdminAuditCmsActions.EmailBlockSetDefault, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/set-default")]
    public Task<IActionResult> SetDefault(Guid id, CancellationToken ct) => AsActor(actor => _blocks.SetDefaultAsync(actor, id, ct));

    /// <summary>Only a block that was never published and nothing uses.</summary>
    [AdminAudited(AdminAuditCmsActions.EmailBlockDeleted, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock), EntityRouteKey = "id")]
    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => AsActor(actor => _blocks.DeleteAsync(actor, id, ct));

    [HttpGet("{id:guid}/versions")]
    public async Task<IActionResult> Versions(Guid id, CancellationToken ct) => From(await _blocks.ListVersionsAsync(id, ct));

    [AdminAudited(AdminAuditCmsActions.EmailBlockVersionRestored, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/versions/{version:int}/restore")]
    public Task<IActionResult> Restore(Guid id, int version, CancellationToken ct) =>
        AsActor(actor => _blocks.RestoreVersionAsync(actor, id, version, ct));

    /// <summary>Renders an unsaved block with an email's published content (kind=LAYOUT or PARTIAL).</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromQuery] string kind, [FromBody] EmailBlockPreviewRequest request, CancellationToken ct) =>
        From(await _blocks.PreviewAsync(null, kind, request, ct));

    [AdminAudited(AdminAuditCmsActions.EmailBlockBulkAction, AdminAuditEntityTypes.EmailBlock, typeof(EmailBlock))]
    [HttpPost("bulk")]
    public Task<IActionResult> Bulk([FromBody] EmailBlockBulkRequest request, CancellationToken ct) =>
        AsActor(actor => _blocks.BulkAsync(actor, request, ct));
}
