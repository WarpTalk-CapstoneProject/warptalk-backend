using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.Application.Mappers;

public static class NotificationPreferenceMapper
{
    public static NotificationPreference CreateDefaultEntity(Guid userId)
    {
        return new NotificationPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            NotificationType = NotificationConstants.DefaultNotificationType,
            EmailEnabled = true,
            PushEnabled = true,
            InAppEnabled = true,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static NotificationPreferenceDto ToDto(NotificationPreference p)
    {
        return new NotificationPreferenceDto(
            p.Id,
            p.UserId,
            p.NotificationType ?? NotificationConstants.DefaultNotificationType,
            p.EmailEnabled,
            p.PushEnabled,
            p.InAppEnabled,
            p.UpdatedAt
        );
    }

    public static void ApplyUpdate(NotificationPreference pref, UpdateNotificationPreferenceRequest request)
    {
        if (request.EmailEnabled.HasValue) pref.EmailEnabled = request.EmailEnabled.Value;
        if (request.PushEnabled.HasValue) pref.PushEnabled = request.PushEnabled.Value;
        if (request.InAppEnabled.HasValue) pref.InAppEnabled = request.InAppEnabled.Value;

        pref.UpdatedAt = DateTime.UtcNow;
    }
}
