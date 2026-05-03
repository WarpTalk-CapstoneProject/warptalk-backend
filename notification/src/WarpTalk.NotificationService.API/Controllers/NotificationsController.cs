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
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(INotificationService notificationService, ILogger<NotificationsController> logger)
    {
        _notificationService = notificationService;
        _logger = logger;
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

    [HttpGet]
    public async Task<IActionResult> GetNotifications([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

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

    /// <summary>
    /// INTERNAL/TESTING ONLY: Seeds a mock notification for integration testing.
    /// Simulates what a gRPC call from another service would do.
    /// </summary>
    [HttpPost("internal/seed")]
    public async Task<IActionResult> SeedMockNotification([FromServices] StackExchange.Redis.IConnectionMultiplexer redis, CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _notificationService.CreateNotificationAsync(
            userId, 
            "SYSTEM_ALERT", 
            "Mock Notification " + DateTime.UtcNow.Ticks, 
            "This is a seeded notification for testing.", 
            null, 
            "{}", 
            ct);

        if (!result.IsSuccess || result.Value == null)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        // Simulate gRPC publishing to Redis
        try
        {
            var msg = new WarpTalk.Shared.Models.RealtimeNotificationMessage
            {
                Id = result.Value.Id.ToString(),
                UserId = userId.ToString(),
                Type = result.Value.Type,
                Title = result.Value.Title,
                Content = result.Value.Content,
                ActionUrl = result.Value.ActionUrl ?? string.Empty,
                PayloadJson = result.Value.PayloadJson,
                CreatedAt = result.Value.CreatedAt.ToString("O")
            };
            var json = System.Text.Json.JsonSerializer.Serialize(msg);
            await redis.GetDatabase().PublishAsync(StackExchange.Redis.RedisChannel.Literal("warptalk:notifications:new"), json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish mock notification to Redis");
        }

        return Ok(result.Value);
    }
}
