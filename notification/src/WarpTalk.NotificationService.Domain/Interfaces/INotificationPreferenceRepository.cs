using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface INotificationPreferenceRepository : IGenericRepository<NotificationPreference>
{
    Task<NotificationPreference?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Which of these people turned email off for this notification type. A person with no row for
    /// the type has not opted out.
    /// </summary>
    Task<IReadOnlySet<Guid>> ListEmailOptOutsAsync(IReadOnlyCollection<Guid> userIds, string notificationType, CancellationToken ct = default);
}
