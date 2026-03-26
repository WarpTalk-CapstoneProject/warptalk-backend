using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet("test")]
    [AllowAnonymous]
    public IActionResult Test()
    {
        return Ok(new { message = "Notification Service API up!" });
    }

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _notificationService.GetPreferencesAsync(userId, ct);
        if (!result.IsSuccess)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdateNotificationPreferenceRequest request, CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _notificationService.UpdatePreferencesAsync(userId, request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }
}
