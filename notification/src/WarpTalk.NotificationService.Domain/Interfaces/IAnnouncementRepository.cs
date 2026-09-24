using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Models;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IAnnouncementRepository
{
    Task AddAsync(Announcement announcement, CancellationToken ct = default);

    /// <summary>Tracked, for updating.</summary>
    Task<Announcement?> GetByIdAsync(Guid id, CancellationToken ct = default);

    void Remove(Announcement announcement);

    /// <summary>Newest first, filtered by effective status as of <paramref name="now"/>.</summary>
    Task<(IReadOnlyList<Announcement> Items, int TotalCount)> GetPageAsync(AnnouncementFilter filter, DateTime now, CancellationToken ct = default);

    /// <summary>How many announcements are in each effective status as of <paramref name="now"/>.</summary>
    Task<IReadOnlyDictionary<string, int>> CountByEffectiveStatusAsync(DateTime now, AnnouncementFilter filter, CancellationToken ct = default);

    /// <summary>Tracked, for a bulk action.</summary>
    Task<IReadOnlyList<Announcement>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Every announcement live at <paramref name="now"/>, newest first. Untracked.</summary>
    Task<IReadOnlyList<Announcement>> GetLiveAsync(DateTime now, CancellationToken ct = default);
}
