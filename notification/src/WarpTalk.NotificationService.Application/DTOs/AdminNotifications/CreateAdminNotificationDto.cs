using System;
using System.Collections.Generic;

namespace WarpTalk.NotificationService.Application.DTOs.AdminNotifications;

public record CreateAdminNotificationDto(
    string Title,
    string Content,
    string Type,
    string TargetAudienceMode,
    List<Guid>? SpecificUserIds,
    Guid? SegmentId,
    // Add other type-specific fields here later
    string? ImageUrl = null,
    string? CtaLink = null,
    string? DiscountCode = null,
    string? Severity = null,
    bool? ActionRequired = null,
    DateTime? DowntimeStart = null,
    DateTime? DowntimeEnd = null
);
