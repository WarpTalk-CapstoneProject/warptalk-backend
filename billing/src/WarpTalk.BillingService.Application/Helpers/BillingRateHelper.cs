using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Helpers;

public record BillingRateNotificationRequest(
    IUnitOfWork UnitOfWork,
    INotificationClient? NotificationClient,
    ILogger Logger,
    ServiceRatesDto? OldRates,
    UpdateServiceRatesRequest NewRates
);

public record RateChangeCollectionRequest(
    List<string> Changes,
    RateChangeRequest RateChange
);

public static class BillingRateHelper
{
    public static double GetRate(IConfiguration configuration, string key, double fallback)
    {
        return double.TryParse(configuration[key], NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    public static async Task NotifyWorkspaceOwnersAsync(
        BillingRateNotificationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.NotificationClient is null)
            return;

        try
        {
            var changes = GetRateChanges(request.OldRates, request.NewRates);
            if (changes.Count == 0)
                return;

            var ownerUserIds = await GetActiveSubscriptionOwnerIdsAsync(request.UnitOfWork, request.Logger, cancellationToken);
            if (ownerUserIds.Count == 0)
                return;

            request.Logger.LogInformation(BillingMessageConstants.LogMessages.SendingRateChangeNotifications, ownerUserIds.Count);

            var notifyResult = await request.NotificationClient.SendNotificationsAsync(
                new SendBillingNotificationsRequest(
                    UserIds: ownerUserIds,
                    Type: BillingMessageConstants.Notifications.Types.RateChange,
                    Title: BillingMessageConstants.Notifications.Titles.RatesUpdated,
                    Body: string.Format(BillingMessageConstants.Notifications.Templates.RatesUpdatedBody, string.Join("\n", changes)),
                    ActionUrl: BillingMessageConstants.Notifications.ActionUrls.Billing,
                    Metadata: new Dictionary<string, string>
                    {
                        { BillingMessageConstants.Notifications.MetadataKeys.ChangedServices, changes.Count.ToString(CultureInfo.InvariantCulture) }
                    }),
                cancellationToken);

            if (!notifyResult.IsSuccess)
                request.Logger.LogWarning(BillingMessageConstants.LogMessages.FailedToNotifyWorkspaceOwnersRateUpdate);
        }
        catch (Exception ex)
        {
            request.Logger.LogWarning(ex, BillingMessageConstants.LogMessages.FailedToNotifyWorkspaceOwnersRateUpdate);
        }
    }

    public static List<string> GetRateChanges(ServiceRatesDto? oldRates, UpdateServiceRatesRequest newRates)
    {
        var changes = new List<string>();
        if (oldRates is null)
            return changes;

        AddRateChange(new RateChangeCollectionRequest(changes, new RateChangeRequest(BillingMessageConstants.Notifications.RateChange.SttLabel, oldRates.SttPerSecond, newRates.SttPerSecond, BillingMessageConstants.Notifications.RateChange.UnitCreditsPerSec)));
        AddRateChange(new RateChangeCollectionRequest(changes, new RateChangeRequest(BillingMessageConstants.Notifications.RateChange.TranslationLabel, oldRates.TranslationPer100Chars, newRates.TranslationPer100Chars, BillingMessageConstants.Notifications.RateChange.UnitCreditsPer100Chars)));
        AddRateChange(new RateChangeCollectionRequest(changes, new RateChangeRequest(BillingMessageConstants.Notifications.RateChange.TtsLabel, oldRates.StandardTtsPerSecond, newRates.StandardTtsPerSecond, BillingMessageConstants.Notifications.RateChange.UnitCreditsPerSec)));
        AddRateChange(new RateChangeCollectionRequest(changes, new RateChangeRequest(BillingMessageConstants.Notifications.RateChange.VoiceCloneLabel, oldRates.VoiceClonePerSecond, newRates.VoiceClonePerSecond, BillingMessageConstants.Notifications.RateChange.UnitCreditsPerSec)));
        AddRateChange(new RateChangeCollectionRequest(changes, new RateChangeRequest(BillingMessageConstants.Notifications.RateChange.AiAssistantInputLabel, oldRates.AiAssistantInputPer1000Tokens, newRates.AiAssistantInputPer1000Tokens, BillingMessageConstants.Notifications.RateChange.UnitCreditsPer1kTokens)));
        AddRateChange(new RateChangeCollectionRequest(changes, new RateChangeRequest(BillingMessageConstants.Notifications.RateChange.AiAssistantOutputLabel, oldRates.AiAssistantOutputPer1000Tokens, newRates.AiAssistantOutputPer1000Tokens, BillingMessageConstants.Notifications.RateChange.UnitCreditsPer1kTokens)));
        return changes;
    }

    public static void AddRateChange(RateChangeCollectionRequest request)
    {
        if (Math.Abs(request.RateChange.OldValue - request.RateChange.NewValue) > 0.0001)
        {
            request.Changes.Add(string.Format(
                BillingMessageConstants.Notifications.RateChange.ChangeTemplate,
                request.RateChange.Label,
                request.RateChange.OldValue,
                request.RateChange.NewValue,
                request.RateChange.Unit));
        }
    }

    public static async Task<IReadOnlyList<Guid>> GetActiveSubscriptionOwnerIdsAsync(
        IUnitOfWork unitOfWork,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var subs = await unitOfWork.Subscriptions.FindAsync(s => s.IsActive && s.DeletedAt == null, cancellationToken);
            return subs.Select(s => s.UserId).Distinct().ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, BillingMessageConstants.LogMessages.ErrorLoadingWorkspaceOwnerIds);
            return Array.Empty<Guid>();
        }
    }
}
