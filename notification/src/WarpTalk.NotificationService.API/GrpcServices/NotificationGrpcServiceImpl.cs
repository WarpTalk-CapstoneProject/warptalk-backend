using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.API.Middlewares;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Application.Mappers;
using Microsoft.Extensions.Logging;

namespace WarpTalk.NotificationService.API.GrpcServices;

/// <summary>
/// gRPC Service handling server-to-server commands.
/// Responsibilities:
/// - Persist newly created notifications to DB and publish events to Redis.
/// - Process MarkAsRead/MarkAllAsRead commands and update the database strictly before UI updates.
/// </summary>
public class NotificationGrpcServiceImpl : NotificationGrpcService.NotificationGrpcServiceBase
{
    private readonly INotificationService _notificationService;
    private readonly StackExchange.Redis.IConnectionMultiplexer _redis;
    private readonly ILogger<NotificationGrpcServiceImpl> _logger;

    public NotificationGrpcServiceImpl(
        INotificationService notificationService,
        StackExchange.Redis.IConnectionMultiplexer redis,
        ILogger<NotificationGrpcServiceImpl> logger)
    {
        _notificationService = notificationService;
        _redis = redis;
        _logger = logger;
    }

    public override async Task<SendNotificationResponse> SendNotification(SendNotificationRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            throw GrpcErrors.Required("User ID");

        if (!Guid.TryParse(request.UserId, out var parsedUserId))
            throw GrpcErrors.InvalidId("User");

        var meta = new Dictionary<string, string>();
        if (request.Metadata != null)
        {
            foreach (var kvp in request.Metadata) meta[kvp.Key] = kvp.Value;
        }

        

        var payloadJson = "{}";
        if (meta.Count > 0)
        {
            payloadJson = System.Text.Json.JsonSerializer.Serialize(meta);
        }

        var validationResult = NotificationValidator.Validate(request.Type, request.Title, request.Body, request.ActionUrl, payloadJson);
        if (!validationResult.IsSuccess)
        {
            _logger.LogWarning("Validation failed for creating notification. User: {UserId}, Type: {Type}, Error: {Error}, ErrorCode: {ErrorCode}", parsedUserId, request.Type, validationResult.Error, validationResult.ErrorCode);
            return new SendNotificationResponse
            {
                Success = false,
                NotificationId = ""
            };
        }

        var dto = NotificationMessageMapper.ToCreateDto(request, parsedUserId, payloadJson);
        var result = await _notificationService.CreateNotificationAsync(dto, context.CancellationToken);

        if (!result.IsSuccess || result.Value == null)
        {
            return new SendNotificationResponse
            {
                Success = false,
                NotificationId = ""
            };
        }

        try
        {
            var msg = WarpTalk.NotificationService.Application.Mappers.NotificationMessageMapper.ToRealtimeDto(result.Value, request.UserId);
            var json = System.Text.Json.JsonSerializer.Serialize(msg);
            await _redis.GetDatabase().PublishAsync(StackExchange.Redis.RedisChannel.Literal(NotificationConstants.RedisNewNotificationChannel), json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish real-time notification to Redis for notification ID: {NotificationId}, User ID: {UserId}", result.Value.Id, request.UserId);
        }

        return new SendNotificationResponse
        {
            Success = true,
            NotificationId = result.Value.Id.ToString()
        };
    }

    public override async Task<GetUserPreferencesResponse> GetUserPreferences(GetUserPreferencesRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var parsedUserId))
            throw GrpcErrors.InvalidId("User");

        var result = await _notificationService.GetPreferencesAsync(parsedUserId, context.CancellationToken);

        var response = new GetUserPreferencesResponse
        {
            UserId = request.UserId
        };

        if (result.IsSuccess && result.Value != null)
        {
            var pref = result.Value;
            response.Preferences.Add(new NotificationChannelPreference
            {
                Channel = pref.NotificationType ?? NotificationConstants.DefaultNotificationType,
                Enabled = pref.EmailEnabled || pref.PushEnabled || pref.InAppEnabled
            });
        }

        return response;
    }

    public override async Task<MarkAsReadResponse> MarkAsRead(MarkAsReadRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
            return new MarkAsReadResponse { Success = false, ErrorMessage = "Invalid User ID", ErrorCode = ErrorCodes.ValidationError };

        if (!Guid.TryParse(request.NotificationId, out var notificationId))
            return new MarkAsReadResponse { Success = false, ErrorMessage = "Invalid Notification ID", ErrorCode = ErrorCodes.ValidationError };

        var result = await _notificationService.MarkAsReadAsync(userId, notificationId, context.CancellationToken);

        return new MarkAsReadResponse
        {
            Success = result.IsSuccess,
            ErrorMessage = result.Error,
            ErrorCode = result.ErrorCode ?? ""
        };
    }

    public override async Task<MarkAllAsReadResponse> MarkAllAsRead(MarkAllAsReadRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var userId))
            return new MarkAllAsReadResponse { Success = false, ErrorMessage = "Invalid User ID", ErrorCode = ErrorCodes.ValidationError };

        var result = await _notificationService.MarkAllAsReadAsync(userId, context.CancellationToken);

        return new MarkAllAsReadResponse
        {
            Success = result.IsSuccess,
            ErrorMessage = result.Error,
            ErrorCode = result.ErrorCode ?? ""
        };
    }
}
