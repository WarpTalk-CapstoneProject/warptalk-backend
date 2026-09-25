using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class AnnouncementViewerStateRepository : IAnnouncementViewerStateRepository
{
    private readonly NotificationDbContext _context;

    public AnnouncementViewerStateRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<Guid, AnnouncementViewerState>> GetForUserAsync(
        Guid userId, IReadOnlyCollection<Guid> announcementIds, CancellationToken ct = default)
    {
        if (announcementIds.Count == 0) return new Dictionary<Guid, AnnouncementViewerState>();
        var ids = announcementIds.ToList();
        var rows = await _context.AnnouncementViewerStates
            .AsNoTracking()
            .Where(s => s.UserId == userId && ids.Contains(s.AnnouncementId))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.AnnouncementId);
    }

    public Task<AnnouncementViewerState?> GetAsync(Guid announcementId, Guid userId, CancellationToken ct = default) =>
        _context.AnnouncementViewerStates.FirstOrDefaultAsync(s => s.AnnouncementId == announcementId && s.UserId == userId, ct);

    public async Task AddAsync(AnnouncementViewerState state, CancellationToken ct = default) =>
        await _context.AnnouncementViewerStates.AddAsync(state, ct);

    public async Task<AnnouncementViewerTotals> GetTotalsAsync(Guid announcementId, CancellationToken ct = default)
    {
        var rows = _context.AnnouncementViewerStates.AsNoTracking().Where(s => s.AnnouncementId == announcementId);
        var totals = await rows
            .GroupBy(_ => 1)
            .Select(group => new AnnouncementViewerTotals(
                group.Count(s => s.ImpressionCount > 0),
                group.Sum(s => s.ImpressionCount),
                group.Count(s => s.DismissedAt != null),
                group.Count(s => s.CtaClickCount > 0),
                group.Sum(s => s.CtaClickCount),
                group.Sum(s => s.SecondaryClickCount)))
            .FirstOrDefaultAsync(ct);
        return totals ?? new AnnouncementViewerTotals(0, 0, 0, 0, 0, 0);
    }
}
