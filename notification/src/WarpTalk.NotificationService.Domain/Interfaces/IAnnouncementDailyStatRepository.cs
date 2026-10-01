using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IAnnouncementDailyStatRepository
{
    /// <summary>Adds to one day's counters in a single atomic upsert; runs immediately.</summary>
    Task IncrementAsync(Guid announcementId, DateOnly day, string eventType, CancellationToken ct = default);

    /// <summary>Untracked, oldest first.</summary>
    Task<IReadOnlyList<AnnouncementDailyStat>> ListSinceAsync(Guid announcementId, DateOnly since, CancellationToken ct = default);
}
