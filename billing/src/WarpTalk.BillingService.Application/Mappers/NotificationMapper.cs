using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Models;

namespace WarpTalk.BillingService.Application.Mappers;

public static class NotificationMapper
{
    public static RealtimeNotificationMessage ToCreditsUpdatedMessage(Guid userId, int newBalance, string title, string content)
    {
        return new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId.ToString(),
            Type = BillingMessageConstants.Notifications.Types.CreditsUpdated,
            Title = title,
            Content = content,
            PayloadJson = JsonSerializer.Serialize(new { new_balance = newBalance }),
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
    }

    public static RealtimeNotificationMessage ToSubscriptionChangedMessage(Guid userId, string action, string planName)
    {
        return new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId.ToString(),
            Type = BillingMessageConstants.Notifications.Types.SubscriptionChanged,
            Title = SubscriptionTitleFor(action),
            Content = string.Format(BillingMessageConstants.Notifications.Templates.SubscriptionChangedContent, action, planName),
            PayloadJson = "{}",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
    }

    /// <summary>
    /// WT-599: the title says which of the three things happened.
    ///
    /// Unknown actions keep the generic title rather than inventing one — a new action added
    /// without a title here should read as vague, not as the wrong event.
    /// </summary>
    private static string SubscriptionTitleFor(string action) => action switch
    {
        BillingMessageConstants.Notifications.ActionCreated =>
            BillingMessageConstants.Notifications.Titles.SubscriptionStarted,
        BillingMessageConstants.Notifications.ActionCancelled =>
            BillingMessageConstants.Notifications.Titles.SubscriptionCancelled,
        _ => BillingMessageConstants.Notifications.Titles.SubscriptionUpdated,
    };

    public static RealtimeNotificationMessage ToPlanChangedMessage(string action, string planName, string? details)
    {
        var content = string.Format(BillingMessageConstants.Notifications.Templates.PlanChangedContent, planName, action);
        if (!string.IsNullOrWhiteSpace(details))
        {
            content += string.Format(BillingMessageConstants.Notifications.Templates.PlanChangedDetails, details);
        }

        return new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = BillingMessageConstants.Notifications.AllUsers,
            Type = BillingMessageConstants.Notifications.Types.PlanChanged,
            Title = BillingMessageConstants.Notifications.Titles.PlanUpdated,
            Content = content,
            PayloadJson = "{}",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
    }
}
