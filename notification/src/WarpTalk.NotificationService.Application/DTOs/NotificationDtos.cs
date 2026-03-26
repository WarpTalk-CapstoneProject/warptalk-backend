namespace WarpTalk.NotificationService.Application.DTOs;

public record NotificationPreferenceDto(
    Guid Id,
    Guid UserId,
    string NotificationType,
    bool EmailEnabled,
    bool PushEnabled,
    bool InAppEnabled,
    DateTime UpdatedAt
);

public record UpdateNotificationPreferenceRequest(
    bool? EmailEnabled,
    bool? PushEnabled,
    bool? InAppEnabled
);
