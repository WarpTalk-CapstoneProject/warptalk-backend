using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.Interfaces;

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

    /// <summary>Live now, meant for the caller, and not dismissed by them.</summary>
    [HttpGet]
    public async Task<IActionResult> Active(CancellationToken ct)
    {
        if (UserId() is not { } userId) return Unauthorized();
        var result = await _announcements.GetActiveForViewerAsync(userId, ct);
        return result.IsSuccess ? Ok(result.Value) : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    [HttpPost("{id:guid}/dismiss")]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken ct)
    {
        if (UserId() is not { } userId) return Unauthorized();
        var result = await _announcements.DismissAsync(userId, id, ct);
        return result.IsSuccess ? NoContent() : ControllerResults.Failure(this, result.Error, result.ErrorCode);
    }

    private Guid? UserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
