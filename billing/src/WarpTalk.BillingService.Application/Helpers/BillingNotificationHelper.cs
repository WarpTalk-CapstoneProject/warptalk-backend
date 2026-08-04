using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Models;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Helpers;

public static class BillingNotificationHelper
{
    public static async Task PublishCreditUpdateAsync(
        IBillingMessagePublisher messagePublisher,
        ILogger logger,
        RealtimeNotificationMessage msg,
        CancellationToken cancellationToken)
    {
        try
        {
            await messagePublisher.PublishAsync(BillingMessageConstants.Notifications.Channel, msg, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToPublishRealtimeCreditUpdateForWorkspace, msg.UserId);
        }
    }

    public static async Task PublishPlanUpdateAsync(
        IBillingMessagePublisher messagePublisher,
        ILogger logger,
        string action,
        string planName,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var msg = NotificationMapper.ToPlanChangedMessage(action, planName, details);
            await messagePublisher.PublishAsync(BillingMessageConstants.Notifications.Channel, msg, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToPublishPlanUpdateBroadcast, planName);
        }
    }

    public static async Task PublishSubscriptionUpdateAsync(
        IBillingMessagePublisher messagePublisher,
        ILogger logger,
        Guid userId,
        string action,
        string planName,
        CancellationToken cancellationToken)
    {
        try
        {
            var msg = NotificationMapper.ToSubscriptionChangedMessage(userId, action, planName);
            await messagePublisher.PublishAsync(BillingMessageConstants.Notifications.Channel, msg, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToPublishRealtimeSubscriptionUpdate, userId);
        }
    }
}
