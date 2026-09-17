using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AssistantService.Infrastructure.Clients;

/// <summary>
/// The notification service's SendNotification RPC, which persists a Notification Center row and
/// publishes the realtime event in one call.
/// </summary>
public class UserNotificationGrpcClient : IUserNotificationClient
{
    private readonly NotificationGrpcService.NotificationGrpcServiceClient _client;
    private readonly ILogger<UserNotificationGrpcClient> _logger;

    public UserNotificationGrpcClient(
        NotificationGrpcService.NotificationGrpcServiceClient client,
        ILogger<UserNotificationGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> SendAsync(UserNotification notification, CancellationToken ct = default)
    {
        try
        {
            var request = new SendNotificationRequest
            {
                UserId = notification.UserId.ToString(),
                Type = notification.Type,
                Title = notification.Title,
                Body = notification.Body,
                ActionUrl = notification.ActionUrl,
            };
            foreach (var (key, value) in notification.Metadata) request.Metadata.Add(key, value);

            var response = await _client.SendNotificationAsync(request, cancellationToken: ct);
            // Read, unlike every older producer in this codebase: Success=false is how a type
            // missing from the validator's schema table shows itself, and ignoring it is how
            // MEETING_INVITED spent months logged as sent and never stored.
            return response.Success;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(
                ex,
                "Could not send {Type} notification to user {UserId}; the change it describes is already committed.",
                notification.Type,
                notification.UserId);
            return false;
        }
    }
}
