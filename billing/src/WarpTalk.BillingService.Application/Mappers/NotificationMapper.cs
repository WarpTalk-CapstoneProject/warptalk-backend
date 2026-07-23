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
            Type = BillingConstants.Notifications.Types.CreditsUpdated,
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
            Type = BillingConstants.Notifications.Types.SubscriptionChanged,
            Title = BillingConstants.Notifications.Titles.SubscriptionUpdated,
            Content = string.Format(BillingConstants.Notifications.Templates.SubscriptionChangedContent, action, planName),
            PayloadJson = "{}",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
    }

    public static RealtimeNotificationMessage ToPlanChangedMessage(string action, string planName, string? details)
    {
        var content = string.Format(BillingConstants.Notifications.Templates.PlanChangedContent, planName, action);
        if (!string.IsNullOrWhiteSpace(details))
        {
            content += $" Details: {details}";
        }

        return new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "all",
            Type = BillingConstants.Notifications.Types.PlanChanged,
            Title = BillingConstants.Notifications.Titles.PlanUpdated,
            Content = content,
            PayloadJson = "{}",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
    }
}
