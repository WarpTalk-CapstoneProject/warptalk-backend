using WarpTalk.Shared;
using WarpTalk.NotificationService.Application.DTOs;

namespace WarpTalk.NotificationService.Application.Interfaces;

public interface INotificationService
{
    Task<Result<NotificationPreferenceDto>> GetPreferencesAsync(Guid userId, CancellationToken ct = default);
    Task<Result<NotificationPreferenceDto>> UpdatePreferencesAsync(Guid userId, UpdateNotificationPreferenceRequest request, CancellationToken ct = default);
    Task<Result> SendNotificationAsync(Guid userId, string templateCode, Dictionary<string, string> variables, CancellationToken ct = default); // Mock
}
