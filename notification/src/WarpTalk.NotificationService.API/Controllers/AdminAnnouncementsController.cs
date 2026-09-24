using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>
/// The announcements CMS. Under /admin/notifications so it rides the gateway route the portal
/// already has for this service rather than adding one to the approved admin list.
/// </summary>
[ApiController]
[Route("api/v1/admin/notifications/announcements")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public sealed class AdminAnnouncementsController : CmsControllerBase
{
    private readonly IAnnouncementService _announcements;

    public AdminAnnouncementsController(IAnnouncementService announcements)
    {
        _announcements = announcements;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] AdminAnnouncementListQuery query, CancellationToken ct) =>
        From(await _announcements.ListAsync(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => From(await _announcements.GetAsync(id, ct));

    [HttpGet("{id:guid}/analytics")]
    public async Task<IActionResult> Analytics(Guid id, [FromQuery] int days = 30, CancellationToken ct = default) =>
        From(await _announcements.GetAnalyticsAsync(id, days, ct));

    [AdminAudited(AdminAuditCmsActions.AnnouncementCreated, AdminAuditEntityTypes.Announcement, typeof(Announcement))]
    [HttpPost]
    public Task<IActionResult> Create([FromBody] UpsertAnnouncementRequest request, CancellationToken ct) =>
        AsActor(actor => _announcements.CreateAsync(actor, request, ct),
            created => Created($"/api/v1/admin/notifications/announcements/{created.Id}", created));

    [AdminAudited(AdminAuditCmsActions.AnnouncementUpdated, AdminAuditEntityTypes.Announcement, typeof(Announcement), EntityRouteKey = "id")]
    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] UpsertAnnouncementRequest request, CancellationToken ct) =>
        AsActor(actor => _announcements.UpdateAsync(actor, id, request, ct));

    /// <summary>Publishes now (no startsAt) or schedules (a future startsAt).</summary>
    [AdminAudited(AdminAuditCmsActions.AnnouncementPublished, AdminAuditEntityTypes.Announcement, typeof(Announcement), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/publish")]
    public Task<IActionResult> Publish(Guid id, [FromBody] PublishAnnouncementRequest request, CancellationToken ct) =>
        AsActor(actor => _announcements.PublishAsync(actor, id, request, ct));

    [AdminAudited(AdminAuditCmsActions.AnnouncementUnpublished, AdminAuditEntityTypes.Announcement, typeof(Announcement), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/unpublish")]
    public Task<IActionResult> Unpublish(Guid id, CancellationToken ct) => AsActor(actor => _announcements.UnpublishAsync(actor, id, ct));

    [AdminAudited(AdminAuditCmsActions.AnnouncementArchived, AdminAuditEntityTypes.Announcement, typeof(Announcement), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/archive")]
    public Task<IActionResult> Archive(Guid id, CancellationToken ct) => AsActor(actor => _announcements.ArchiveAsync(actor, id, ct));

    [AdminAudited(AdminAuditCmsActions.AnnouncementDuplicated, AdminAuditEntityTypes.Announcement, typeof(Announcement), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/duplicate")]
    public Task<IActionResult> Duplicate(Guid id, CancellationToken ct) =>
        AsActor(actor => _announcements.DuplicateAsync(actor, id, ct),
            created => Created($"/api/v1/admin/notifications/announcements/{created.Id}", created));

    /// <summary>Drafts only. A published announcement is archived, so there is a record it ran.</summary>
    [AdminAudited(AdminAuditCmsActions.AnnouncementDeleted, AdminAuditEntityTypes.Announcement, typeof(Announcement), EntityRouteKey = "id")]
    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => AsActor(actor => _announcements.DeleteAsync(actor, id, ct));

    [AdminAudited(AdminAuditCmsActions.AnnouncementBulkAction, AdminAuditEntityTypes.Announcement, typeof(Announcement))]
    [HttpPost("bulk")]
    public Task<IActionResult> Bulk([FromBody] AnnouncementBulkRequest request, CancellationToken ct) =>
        AsActor(actor => _announcements.BulkAsync(actor, request, ct));

    /// <summary>An image for an announcement (multipart, one file named "file").</summary>
    [AdminAudited(AdminAuditCmsActions.AnnouncementAssetAdded, AdminAuditEntityTypes.Announcement, typeof(AnnouncementAsset))]
    [HttpPost("assets")]
    [RequestSizeLimit(AnnouncementConstants.MaxAssetBytes + 64 * 1024)]
    public async Task<IActionResult> UploadAsset(IFormFile file, CancellationToken ct)
    {
        if (file is null) return BadRequest(new WarpTalk.Shared.ApiErrorResponse("Attach an image as \"file\".", WarpTalk.Shared.ErrorCodes.ValidationError));
        if (file.Length > AnnouncementConstants.MaxAssetBytes)
            return BadRequest(new WarpTalk.Shared.ApiErrorResponse("Images must be 2 MB or smaller.", WarpTalk.Shared.ErrorCodes.ValidationError));

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        return await AsActor(actor => _announcements.UploadAssetAsync(actor, file.FileName, file.ContentType, bytes, ct),
            asset => Created(asset.Url, asset));
    }
}
