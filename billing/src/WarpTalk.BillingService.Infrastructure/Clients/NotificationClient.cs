using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.Infrastructure.Clients;

public class NotificationClient : INotificationClient
{
    private readonly NotificationGrpcService.NotificationGrpcServiceClient _grpcClient;
    private readonly ILogger<NotificationClient> _logger;

    public NotificationClient(
        NotificationGrpcService.NotificationGrpcServiceClient grpcClient,
        ILogger<NotificationClient> logger)
    {
        _grpcClient = grpcClient;
        _logger = logger;
    }

    public async Task SendNotificationAsync(
        Guid userId,
        string type,
        string title,
        string body,
        string actionUrl,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new SendNotificationRequest
            {
                UserId = userId.ToString(),
                Type = type,
                Title = title,
                Body = body,
                ActionUrl = actionUrl
            };

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Metadata[kvp.Key] = kvp.Value;
                }
            }

            await _grpcClient.SendNotificationAsync(request, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification via gRPC client to user {UserId}", userId);
        }
    }
}
