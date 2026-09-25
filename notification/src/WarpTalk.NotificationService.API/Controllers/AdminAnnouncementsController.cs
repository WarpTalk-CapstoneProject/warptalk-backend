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
[RequirePermission(AdminPermissions.ContentAnnouncements)]
public sealed class AdminAnnouncementsController : CmsControllerBase
{
    private readonly IAnnouncementService _announcements;
    private readonly IStaffAccessResolver? _access;

    public AdminAnnouncementsController(IAnnouncementService announcements, IStaffAccessResolver? access = null)
    {
        _announcements = announcements;
        _access = access;
    }

    /// <summary>
    /// An announcement with an email channel is an audience send, so choosing that channel — and
    /// publishing an announcement that has one — also needs content.email_send.
    /// </summary>
    private async Task<bool> MaySendEmailAsync(CancellationToken ct) =>
        _access is null || await _access.HasPermissionAsync(User, AdminPermissions.ContentEmailSend, ct);

    private IActionResult EmailForbidden() =>
        ControllerResults.Failure(this, "Sending an announcement by email needs the content.email_send permission.", WarpTalk.Shared.ErrorCodes.Forbidden);

    private async Task<bool> SendsEmailAsync(Guid id, CancellationToken ct)
    {
        var current = await _announcements.GetAsync(id, ct);
        return current.IsSuccess && current.Value!.EmailTemplateKey is not null;
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
    public async Task<IActionResult> Create([FromBody] UpsertAnnouncementRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.EmailTemplateKey) && !await MaySendEmailAsync(ct)) return EmailForbidden();
        return await AsActor(actor => _announcements.CreateAsync(actor, request, ct),
            created => Created($"/api/v1/admin/notifications/announcements/{created.Id}", created));
    }

    [AdminAudited(AdminAuditCmsActions.AnnouncementUpdated, AdminAuditEntityTypes.Announcement, typeof(Announcement), EntityRouteKey = "id")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertAnnouncementRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.EmailTemplateKey))
        {
            var current = await _announcements.GetAsync(id, ct);
            var changed = !current.IsSuccess
                || !string.Equals(current.Value!.EmailTemplateKey, request.EmailTemplateKey.Trim().ToLowerInvariant(), StringComparison.Ordinal);
            if (changed && !await MaySendEmailAsync(ct)) return EmailForbidden();
        }
        return await AsActor(actor => _announcements.UpdateAsync(actor, id, request, ct));
    }

    /// <summary>Publishes now (no startsAt) or schedules (a future startsAt).</summary>
    [AdminAudited(AdminAuditCmsActions.AnnouncementPublished, AdminAuditEntityTypes.Announcement, typeof(Announcement), EntityRouteKey = "id")]
    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, [FromBody] PublishAnnouncementRequest request, CancellationToken ct)
    {
        if (await SendsEmailAsync(id, ct) && !await MaySendEmailAsync(ct)) return EmailForbidden();
        return await AsActor(actor => _announcements.PublishAsync(actor, id, request, ct));
    }

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
    public async Task<IActionResult> Bulk([FromBody] AnnouncementBulkRequest request, CancellationToken ct)
    {
        if (string.Equals(request.Action, "publish", StringComparison.OrdinalIgnoreCase) && !await MaySendEmailAsync(ct))
        {
            foreach (var id in (request.Ids ?? []).Distinct().Take(AnnouncementConstants.MaxBulkItems))
            {
                if (await SendsEmailAsync(id, ct)) return EmailForbidden();
            }
        }
        return await AsActor(actor => _announcements.BulkAsync(actor, request, ct));
    }

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
