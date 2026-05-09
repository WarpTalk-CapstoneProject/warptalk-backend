using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly IAdminNotificationService _adminNotificationService;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        INotificationService notificationService, 
        IAdminNotificationService adminNotificationService,
        ILogger<NotificationsController> logger)
    {
        _notificationService = notificationService;
        _adminNotificationService = adminNotificationService;
        _logger = logger;
    }

    // [HttpGet("test")]
    // [AllowAnonymous]
    // public IActionResult Test()
    // {
    //     return Ok(new { message = "Notification Service API up!" });
    // }

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
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [HttpGet]
    public async Task<IActionResult> GetNotifications([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        page = Math.Max(1, page);
        pageSize = Math.Max(1, Math.Min(pageSize, 100));

        var result = await _notificationService.GetNotificationsAsync(userId, page, pageSize, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [HttpPatch("{id}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id, CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _notificationService.MarkAsReadAsync(userId, id, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return NoContent();
    }

    [HttpPatch("read-all")]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _notificationService.MarkAllAsReadAsync(userId, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }
//Temporary endpoint for seeding mock notifications to verify DB integration and testing.
    [HttpPost("internal/seed")]
    public async Task<IActionResult> SeedMockNotification(CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var dto = new WarpTalk.NotificationService.Application.DTOs.CreateNotificationMessageDto(
            userId,
            "SYSTEM_ALERT",
            "Mock Notification",
            "This is a seeded notification for testing purposes.",
            null,
            "{}"
        );

        var result = await _notificationService.CreateNotificationAsync(dto, ct);

        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(new { id = result.Value.Id });
    }

    [HttpPost("~/api/v1/admin/notifications")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateAdminNotification([FromBody] CreateAdminNotificationDto request, CancellationToken ct)
    {
        var adminIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminIdString) || !Guid.TryParse(adminIdString, out var adminId))
            return Unauthorized();

        var result = await _adminNotificationService.CreateAdminNotificationAsync(adminId, request, ct);

        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Created($"/api/v1/admin/notifications/{result.Value!.Id}", result.Value);
    }

    [HttpGet("~/api/v1/admin/notifications")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAdminNotifications([FromQuery] GetAdminNotificationsQuery query, CancellationToken ct)
    {
        var adminIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminIdString) || !Guid.TryParse(adminIdString, out _))
            return Unauthorized();

        var result = await _adminNotificationService.GetAdminNotificationsAsync(query, ct);
        
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [HttpGet("~/api/v1/admin/notifications/{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAdminNotificationDetail(Guid id, CancellationToken ct)
    {
        var adminIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminIdString) || !Guid.TryParse(adminIdString, out _))
            return Unauthorized();

        var result = await _adminNotificationService.GetAdminNotificationDetailAsync(id, ct);
        
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) 
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }
}
