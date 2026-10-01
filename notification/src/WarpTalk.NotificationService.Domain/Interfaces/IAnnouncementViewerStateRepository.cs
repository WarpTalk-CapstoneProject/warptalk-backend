using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IAnnouncementViewerStateRepository
{
    /// <summary>Untracked. This person's state for each of <paramref name="announcementIds"/> that has one.</summary>
    Task<IReadOnlyDictionary<Guid, AnnouncementViewerState>> GetForUserAsync(
        Guid userId, IReadOnlyCollection<Guid> announcementIds, CancellationToken ct = default);

    /// <summary>Tracked, for updating.</summary>
    Task<AnnouncementViewerState?> GetAsync(Guid announcementId, Guid userId, CancellationToken ct = default);

    Task AddAsync(AnnouncementViewerState state, CancellationToken ct = default);

    /// <summary>Totals across every viewer of one announcement.</summary>
    Task<AnnouncementViewerTotals> GetTotalsAsync(Guid announcementId, CancellationToken ct = default);
}

public sealed record AnnouncementViewerTotals(
    int UniqueViewers,
    int Impressions,
    int Dismissals,
    int UniqueCtaClickers,
    int CtaClicks,
    int SecondaryClicks);
