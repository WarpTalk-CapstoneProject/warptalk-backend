using System;

namespace WarpTalk.NotificationService.Application.DTOs.AdminNotifications;

public record DeliveryEventPayload(
    Guid NotificationId,
    string TargetAudienceMode,
    Guid[]? SpecificUserIds
);
