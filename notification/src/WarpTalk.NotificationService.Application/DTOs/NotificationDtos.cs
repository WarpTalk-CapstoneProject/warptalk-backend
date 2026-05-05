using WarpTalk.NotificationService.Domain.Entities;

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

public static class NotificationMappingExtensions
{
    public static NotificationPreferenceDto ToDto(this NotificationPreference p) =>
        new NotificationPreferenceDto(
            p.Id,
            p.UserId,
            p.NotificationType ?? "SYSTEM",
            p.EmailEnabled,
            p.PushEnabled,
            p.InAppEnabled,
            p.UpdatedAt
        );
}

public record UpdateNotificationPreferenceRequest(
    bool? EmailEnabled,
    bool? PushEnabled,
    bool? InAppEnabled
);

public record NotificationMessageDto(
    Guid Id,
    string Type,
    string Title,
    string Content,
    string? ActionUrl,
    string PayloadJson,
    bool IsRead,
    DateTime? ReadAt,
    DateTime CreatedAt
);

public record NotificationPaginatedResponse(
    IEnumerable<NotificationMessageDto> Items,
    int TotalCount,
    int Page,
    int PageSize
);
