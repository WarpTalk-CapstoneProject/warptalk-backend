using System;
using System.Collections.Generic;

namespace WarpTalk.NotificationService.Application.DTOs.AdminNotifications;

public record AdminNotificationSummaryDto(
    Guid Id,
    string Title,
    string Type,
    string Status,
    string TargetAudienceMode,
    Guid CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record AdminNotificationDetailDto(
    Guid Id,
    string Title,
    string Content,
    string Type,
    string Status,
    string TargetAudienceMode,
    string TargetAudienceData, // JSON string
    string Payload, // JSON string
    Guid CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record AdminNotificationPaginatedResponse(
    IEnumerable<AdminNotificationSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize
);

public record GetAdminNotificationsQuery(
    int Page = 1,
    int PageSize = 50,
    string? Title = null,
    string? Type = null,
    string? Status = null,
    DateTime? CreatedFrom = null,
    DateTime? CreatedTo = null
);
