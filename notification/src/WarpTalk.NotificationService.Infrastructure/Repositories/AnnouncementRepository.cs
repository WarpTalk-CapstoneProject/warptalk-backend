using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Models;
using WarpTalk.NotificationService.Domain.Rules;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class AnnouncementRepository : IAnnouncementRepository
{
    private readonly NotificationDbContext _context;

    public AnnouncementRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Announcement announcement, CancellationToken ct = default) =>
        await _context.Announcements.AddAsync(announcement, ct);

    public Task<Announcement?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _context.Announcements.FirstOrDefaultAsync(a => a.Id == id, ct);

    public void Remove(Announcement announcement) => _context.Announcements.Remove(announcement);

    public async Task<(IReadOnlyList<Announcement> Items, int TotalCount)> GetPageAsync(
        AnnouncementFilter filter, DateTime now, CancellationToken ct = default)
    {
        var query = Searched(filter.Search);
        if (!string.IsNullOrWhiteSpace(filter.EffectiveStatus))
            query = query.Where(AnnouncementLifecycle.HasEffectiveStatus(filter.EffectiveStatus, now));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByEffectiveStatusAsync(
        DateTime now, string? search, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var status in AnnouncementConstants.EffectiveStatuses)
        {
            counts[status] = await Searched(search)
                .CountAsync(AnnouncementLifecycle.HasEffectiveStatus(status, now), ct);
        }
        return counts;
    }

    public async Task<IReadOnlyList<Announcement>> GetLiveAsync(DateTime now, CancellationToken ct = default) =>
        await _context.Announcements
            .AsNoTracking()
            .Where(AnnouncementLifecycle.HasEffectiveStatus(AnnouncementConstants.EffectivePublished, now))
            .OrderByDescending(a => a.StartsAt ?? a.PublishedAt)
            .ThenByDescending(a => a.Id)
            .Take(100)
            .ToListAsync(ct);

    private IQueryable<Announcement> Searched(string? search)
    {
        var query = _context.Announcements.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(a => EF.Functions.ILike(a.Title, pattern, "\\"));
        }
        return query;
    }
}
