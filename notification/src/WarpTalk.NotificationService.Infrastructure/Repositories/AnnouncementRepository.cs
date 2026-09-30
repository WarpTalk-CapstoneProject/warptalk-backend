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
        var query = Filtered(filter);
        if (!string.IsNullOrWhiteSpace(filter.EffectiveStatus))
            query = query.Where(AnnouncementLifecycle.HasEffectiveStatus(filter.EffectiveStatus, now));

        var total = await query.CountAsync(ct);
        var items = await Sorted(query, filter)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByEffectiveStatusAsync(
        DateTime now, AnnouncementFilter filter, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var status in AnnouncementConstants.EffectiveStatuses)
        {
            counts[status] = await Filtered(filter)
                .CountAsync(AnnouncementLifecycle.HasEffectiveStatus(status, now), ct);
        }
        return counts;
    }

    public async Task<IReadOnlyList<Announcement>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.ToList();
        return await _context.Announcements.Where(a => list.Contains(a.Id)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Announcement>> GetLiveAsync(DateTime now, CancellationToken ct = default) =>
        await _context.Announcements
            .AsNoTracking()
            .Where(AnnouncementLifecycle.HasEffectiveStatus(AnnouncementConstants.EffectivePublished, now))
            .OrderByDescending(a => a.Priority)
            .ThenByDescending(a => a.StartsAt ?? a.PublishedAt)
            .ThenByDescending(a => a.Id)
            .Take(100)
            .ToListAsync(ct);

    /// <summary>Everything but the status: the status tabs count within the other filters.</summary>
    private IQueryable<Announcement> Filtered(AnnouncementFilter filter)
    {
        var query = _context.Announcements.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = $"%{filter.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(a => EF.Functions.ILike(a.Title, pattern, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(filter.Type)) query = query.Where(a => a.Type == filter.Type);
        if (!string.IsNullOrWhiteSpace(filter.Placement)) query = query.Where(a => a.Placement == filter.Placement);
        return query;
    }

    private static IQueryable<Announcement> Sorted(IQueryable<Announcement> query, AnnouncementFilter filter) =>
        (filter.Sort, filter.Descending) switch
        {
            ("created", true) => query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id),
            ("created", false) => query.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id),
            ("priority", true) => query.OrderByDescending(a => a.Priority).ThenByDescending(a => a.UpdatedAt),
            ("priority", false) => query.OrderBy(a => a.Priority).ThenByDescending(a => a.UpdatedAt),
            ("title", true) => query.OrderByDescending(a => a.Title).ThenBy(a => a.Id),
            ("title", false) => query.OrderBy(a => a.Title).ThenBy(a => a.Id),
            ("starts", true) => query.OrderByDescending(a => a.StartsAt ?? a.PublishedAt).ThenByDescending(a => a.Id),
            ("starts", false) => query.OrderBy(a => a.StartsAt ?? a.PublishedAt).ThenBy(a => a.Id),
            (_, false) => query.OrderBy(a => a.UpdatedAt).ThenBy(a => a.Id),
            _ => query.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Id),
        };
}
