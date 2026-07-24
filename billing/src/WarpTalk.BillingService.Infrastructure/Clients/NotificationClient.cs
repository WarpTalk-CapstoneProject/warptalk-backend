using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Helpers;
using WarpTalk.Shared;
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

    public async Task<Result> SendNotificationsAsync(
        SendBillingNotificationsRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tasks = request.UserIds.Select(userId => NotificationClientHelper.SendSingleNotificationAsync(
                _grpcClient,
                _logger,
                new SendSingleNotificationRequest(
                    userId,
                    request.Type,
                    request.Title,
                    request.Body,
                    request.ActionUrl,
                    request.Metadata),
                cancellationToken));
            await Task.WhenAll(tasks);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToSendNotificationsToUsers);
            return Result.Failure(ex.Message, ErrorCodes.InternalServerError);
        }
    }
}
