using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.GrpcServices;

public class GatewayRealtimeServiceImpl : GatewayRealtimeService.GatewayRealtimeServiceBase
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<GatewayRealtimeServiceImpl> _logger;

    public GatewayRealtimeServiceImpl(IHubContext<NotificationHub> hubContext, ILogger<GatewayRealtimeServiceImpl> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public override async Task<PushNewNotificationResponse> PushNewNotification(PushNewNotificationRequest request, ServerCallContext context)
    {
        try
        {
            if (string.IsNullOrEmpty(request.UserId))
            {
                return new PushNewNotificationResponse { Success = false };
            }

            var groupName = $"user:{request.UserId}";
            await _hubContext.Clients.Group(groupName).SendAsync("NewNotification", request);
            _logger.LogDebug("GatewayRealtimeService: Broadcasted NewNotification to {GroupName}", groupName);

            return new PushNewNotificationResponse { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GatewayRealtimeService: Failed to broadcast new notification.");
            return new PushNewNotificationResponse { Success = false };
        }
    }
}
