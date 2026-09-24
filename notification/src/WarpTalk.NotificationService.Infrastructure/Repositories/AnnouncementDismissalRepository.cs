using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class AnnouncementDismissalRepository : IAnnouncementDismissalRepository
{
    private readonly NotificationDbContext _context;

    public AnnouncementDismissalRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlySet<Guid>> GetDismissedIdsAsync(
        Guid userId, IReadOnlyCollection<Guid> announcementIds, CancellationToken ct = default)
    {
        if (announcementIds.Count == 0) return new HashSet<Guid>();
        var ids = announcementIds.ToList();
        var dismissed = await _context.AnnouncementDismissals
            .AsNoTracking()
            .Where(d => d.UserId == userId && ids.Contains(d.AnnouncementId))
            .Select(d => d.AnnouncementId)
            .ToListAsync(ct);
        return dismissed.ToHashSet();
    }

    public Task<bool> ExistsAsync(Guid announcementId, Guid userId, CancellationToken ct = default) =>
        _context.AnnouncementDismissals.AnyAsync(d => d.AnnouncementId == announcementId && d.UserId == userId, ct);

    public async Task AddAsync(AnnouncementDismissal dismissal, CancellationToken ct = default) =>
        await _context.AnnouncementDismissals.AddAsync(dismissal, ct);
}
