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
    /// <summary>
    /// <c>billing.credits_updated</c>. The payload keeps <c>new_balance</c> (the original shape) and,
    /// since WT-878, says which workspace it is about and the service state the balance left it
    /// in — a top-up that lifted an overage suspension is exactly the moment the billing page needs
    /// to stop showing "suspended". The extra keys are optional: a reader of the old shape is
    /// unaffected.
    /// </summary>
    public static RealtimeNotificationMessage ToCreditsUpdatedMessage(
        Guid userId,
        int newBalance,
        string title,
        string content,
        Guid? workspaceId = null,
        string? serviceState = null,
        string? suspendedReason = null)
    {
        object payload = workspaceId is null && serviceState is null
            ? new { new_balance = newBalance }
            : new
            {
                new_balance = newBalance,
                workspace_id = workspaceId?.ToString(),
                service_state = serviceState,
                suspended_reason = suspendedReason,
            };

        return new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId.ToString(),
            Type = BillingMessageConstants.Notifications.Types.CreditsUpdated,
            Title = title,
            Content = content,
            PayloadJson = JsonSerializer.Serialize(payload),
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
