using System;
using System.Text.Json;
using WarpTalk.BillingService.Domain.Constants;
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
            Title = BillingMessageConstants.Notifications.Titles.SubscriptionUpdated,
            Content = string.Format(BillingMessageConstants.Notifications.Templates.SubscriptionChangedContent, action, planName),
            PayloadJson = "{}",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
    }

    public static RealtimeNotificationMessage ToPlanChangedMessage(string action, string planName, string? details)
    {
        var content = string.Format(BillingMessageConstants.Notifications.Templates.PlanChangedContent, planName, action);
        if (!string.IsNullOrWhiteSpace(details))
        {
            content += $" Details: {details}";
        }

        return new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "all",
            Type = BillingMessageConstants.Notifications.Types.PlanChanged,
            Title = BillingMessageConstants.Notifications.Titles.PlanUpdated,
            Content = content,
            PayloadJson = "{}",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
    }
}
