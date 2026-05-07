using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface INotificationPreferenceRepository : IGenericRepository<NotificationPreference>
{
    Task<NotificationPreference?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
}
