using WarpTalk.Shared;
using WarpTalk.NotificationService.Application.DTOs;

namespace WarpTalk.NotificationService.Application.Interfaces;

public interface INotificationService
{
    Task<Result<NotificationPreferenceDto>> GetPreferencesAsync(Guid userId, CancellationToken ct = default);
    Task<Result<NotificationPreferenceDto>> UpdatePreferencesAsync(Guid userId, UpdateNotificationPreferenceRequest request, CancellationToken ct = default);
    Task<Result> SendNotificationAsync(Guid userId, string templateCode, Dictionary<string, string> variables, CancellationToken ct = default); // Mock
    Task<Result<NotificationPaginatedResponse>> GetNotificationsAsync(Guid userId, int page = 1, int pageSize = 50, CancellationToken ct = default);
    Task<Result> MarkAsReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default);
    Task<Result> MarkAllAsReadAsync(Guid userId, CancellationToken ct = default);
    Task<Result<NotificationMessageDto>> CreateNotificationAsync(Guid userId, string type, string title, string content, string? actionUrl, string payloadJson, CancellationToken ct = default);
}
