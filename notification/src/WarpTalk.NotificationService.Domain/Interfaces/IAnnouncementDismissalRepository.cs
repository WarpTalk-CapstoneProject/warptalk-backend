using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IAnnouncementDismissalRepository
{
    /// <summary>Of <paramref name="announcementIds"/>, the ones this person has dismissed.</summary>
    Task<IReadOnlySet<Guid>> GetDismissedIdsAsync(Guid userId, IReadOnlyCollection<Guid> announcementIds, CancellationToken ct = default);

    Task<bool> ExistsAsync(Guid announcementId, Guid userId, CancellationToken ct = default);

    /// <summary>Records a dismissal. Callers check <see cref="ExistsAsync"/> first.</summary>
    Task AddAsync(AnnouncementDismissal dismissal, CancellationToken ct = default);
}
