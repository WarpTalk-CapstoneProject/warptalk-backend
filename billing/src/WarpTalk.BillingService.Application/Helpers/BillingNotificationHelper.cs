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
    /// <summary>WT-878: title of <c>billing.credits_updated</c>.</summary>
    public const string CreditsUpdatedTitle = "Credits updated";

    /// <summary>WT-878: {0} is the new balance.</summary>
    public const string CreditsUpdatedContentTemplate = "Your workspace's credit balance is now {0:N0} credits.";

    /// <summary>WT-878: {0} is the new balance; used when the same change lifted a suspension.</summary>
    public const string CreditsUpdatedResumedContentTemplate =
        "Your workspace's credit balance is now {0:N0} credits, and AI services have resumed.";

    /// <summary>
    /// WT-878: <c>billing.credits_updated</c> for one subscription, addressed to its owner — the
    /// gateway relays a billing notification to the single user id it names
    /// (BillingRedisSubscriberService), so other workspace members do not receive it.
    /// Best-effort: logged, never thrown.
    /// </summary>
    public static Task PublishCreditsUpdatedAsync(
        IBillingMessagePublisher messagePublisher,
        ILogger logger,
        Subscription subscription,
        bool resumed,
        CancellationToken cancellationToken)
    {
        var content = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            resumed ? CreditsUpdatedResumedContentTemplate : CreditsUpdatedContentTemplate,
            subscription.CreditsRemaining);

        var msg = NotificationMapper.ToCreditsUpdatedMessage(
            subscription.UserId,
            subscription.CreditsRemaining,
            CreditsUpdatedTitle,
            content,
            subscription.WorkspaceId,
            subscription.ServiceState,
            subscription.SuspendedReason);

        return PublishCreditUpdateAsync(messagePublisher, logger, msg, cancellationToken);
    }

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
