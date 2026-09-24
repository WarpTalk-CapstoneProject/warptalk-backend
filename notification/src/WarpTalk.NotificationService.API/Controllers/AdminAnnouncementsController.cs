using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>
/// The announcements CMS. Under /admin/notifications so it rides the gateway route the portal
/// already has for this service rather than adding one to the approved admin list.
/// </summary>
[ApiController]
[Route("api/v1/admin/notifications/announcements")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public sealed class AdminAnnouncementsController : ControllerBase
{
    private readonly IAnnouncementService _announcements;

    public AdminAnnouncementsController(IAnnouncementService announcements)
    {
        _announcements = announcements;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] AdminAnnouncementListQuery query, CancellationToken ct)
    {
        var result = await _announcements.ListAsync(query, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await _announcements.GetAsync(id, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertAnnouncementRequest request, CancellationToken ct)
    {
        if (AdminId() is not { } adminId) return Unauthorized();
        var result = await _announcements.CreateAsync(adminId, request, ct);
        return result.IsSuccess
            ? Created($"/api/v1/admin/notifications/announcements/{result.Value!.Id}", result.Value)
            : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertAnnouncementRequest request, CancellationToken ct)
    {
        if (AdminId() is not { } adminId) return Unauthorized();
        var result = await _announcements.UpdateAsync(adminId, id, request, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    /// <summary>Publishes now (no startsAt) or schedules (a future startsAt).</summary>
    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, [FromBody] PublishAnnouncementRequest request, CancellationToken ct)
    {
        if (AdminId() is not { } adminId) return Unauthorized();
        var result = await _announcements.PublishAsync(adminId, id, request, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpPost("{id:guid}/unpublish")]
    public async Task<IActionResult> Unpublish(Guid id, CancellationToken ct)
    {
        if (AdminId() is not { } adminId) return Unauthorized();
        var result = await _announcements.UnpublishAsync(adminId, id, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        if (AdminId() is not { } adminId) return Unauthorized();
        var result = await _announcements.ArchiveAsync(adminId, id, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpPost("{id:guid}/duplicate")]
    public async Task<IActionResult> Duplicate(Guid id, CancellationToken ct)
    {
        if (AdminId() is not { } adminId) return Unauthorized();
        var result = await _announcements.DuplicateAsync(adminId, id, ct);
        return result.IsSuccess
            ? Created($"/api/v1/admin/notifications/announcements/{result.Value!.Id}", result.Value)
            : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    /// <summary>Drafts only. A published announcement is archived, so there is a record it ran.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (AdminId() is null) return Unauthorized();
        var result = await _announcements.DeleteAsync(id, ct);
        return result.IsSuccess ? NoContent() : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    private Guid? AdminId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
