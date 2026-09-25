using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>
/// Announcements as the people they are for see them. Under /notifications so they ride the
/// gateway's existing notification route.
/// </summary>
[ApiController]
[Route("api/v1/notifications/announcements")]
[Authorize]
public sealed class AnnouncementsController : ControllerBase
{
    private readonly IAnnouncementService _announcements;

    public AnnouncementsController(IAnnouncementService announcements)
    {
        _announcements = announcements;
    }

    /// <summary>
    /// Live now, meant for the caller, and due under each announcement's show frequency.
    /// <paramref name="locale"/> is the app's UI language; <paramref name="sessionId"/> the browser
    /// session id the frequency rules are measured against.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Active([FromQuery] string? locale, [FromQuery] string? sessionId, CancellationToken ct)
    {
        if (UserId() is not { } userId) return Unauthorized();
        var result = await _announcements.GetActiveForViewerAsync(userId, locale, sessionId, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    /// <summary>IMPRESSION, DISMISS, CTA_CLICK or SECONDARY_CLICK.</summary>
    [HttpPost("{id:guid}/events")]
    public async Task<IActionResult> Event(Guid id, [FromBody] AnnouncementEventRequest request, CancellationToken ct)
    {
        if (UserId() is not { } userId) return Unauthorized();
        var result = await _announcements.RecordEventAsync(userId, id, request, ct);
        return result.IsSuccess ? NoContent() : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    /// <summary>Kept for clients built before CMS v2: the same as a DISMISS event with no session.</summary>
    [HttpPost("{id:guid}/dismiss")]
    public Task<IActionResult> Dismiss(Guid id, CancellationToken ct) =>
        Event(id, new AnnouncementEventRequest(AnnouncementConstants.EventDismiss), ct);

    /// <summary>
    /// An uploaded announcement image. Anonymous on purpose: an &lt;img&gt; cannot carry the bearer
    /// token, and the random id is the capability. Immutable, so browsers cache it for good.
    /// </summary>
    [HttpGet("assets/{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> Asset(Guid id, CancellationToken ct)
    {
        var result = await _announcements.GetAssetAsync(id, ct);
        if (!result.IsSuccess) return NotFound();
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'";
        return File(result.Value!.Content, result.Value.ContentType);
    }

    private Guid? UserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
