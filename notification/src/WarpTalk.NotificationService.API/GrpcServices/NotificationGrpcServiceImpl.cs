using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Application.Interfaces;

namespace WarpTalk.NotificationService.API.GrpcServices;

public class NotificationGrpcServiceImpl : NotificationGrpcService.NotificationGrpcServiceBase
{
    private readonly INotificationService _notificationService;
    private readonly GatewayRealtimeService.GatewayRealtimeServiceClient _gatewayRealtimeClient;

    public NotificationGrpcServiceImpl(
        INotificationService notificationService,
        GatewayRealtimeService.GatewayRealtimeServiceClient gatewayRealtimeClient)
    {
        _notificationService = notificationService;
        _gatewayRealtimeClient = gatewayRealtimeClient;
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

        if (!string.IsNullOrEmpty(request.ActionUrl))
        {
            meta["action_url"] = request.ActionUrl;
        }

        var payloadJson = "{}";
        if (meta.Count > 0)
        {
            payloadJson = System.Text.Json.JsonSerializer.Serialize(meta);
        }

        var result = await _notificationService.CreateNotificationAsync(
            parsedUserId, 
            request.Type, 
            request.Title, 
            request.Body, 
            payloadJson, 
            context.CancellationToken);

        if (!result.IsSuccess)
        {
            return new SendNotificationResponse
            {
                Success = false,
                NotificationId = ""
            };
        }

        try
        {
            await _gatewayRealtimeClient.PushNewNotificationAsync(new PushNewNotificationRequest
            {
                Id = result.Value.Id.ToString(),
                UserId = request.UserId,
                Type = result.Value.Type,
                Title = result.Value.Title,
                Content = result.Value.Content,
                PayloadJson = result.Value.PayloadJson,
                CreatedAt = result.Value.CreatedAt.ToString("O")
            });
        }
        catch
        {
            // Ignore error so notification creation still succeeds
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
                Channel = pref.NotificationType ?? "SYSTEM",
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
