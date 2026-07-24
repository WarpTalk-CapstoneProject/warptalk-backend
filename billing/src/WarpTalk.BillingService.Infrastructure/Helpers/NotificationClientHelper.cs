using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.Infrastructure.Helpers;

public static class NotificationClientHelper
{
    public static async Task SendSingleNotificationAsync(
        NotificationGrpcService.NotificationGrpcServiceClient grpcClient,
        ILogger logger,
        SendSingleNotificationRequest notification,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new SendNotificationRequest
            {
                UserId = notification.UserId.ToString(),
                Type = notification.Type,
                Title = notification.Title,
                Body = notification.Body,
                ActionUrl = notification.ActionUrl
            };

            if (notification.Metadata != null)
            {
                foreach (var kvp in notification.Metadata)
                {
                    request.Metadata[kvp.Key] = kvp.Value;
                }
            }

            await grpcClient.SendNotificationAsync(request, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToSendNotificationViaGrpcToUser, notification.UserId);
        }
    }
}
