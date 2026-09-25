using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class AnnouncementDailyStatRepository : IAnnouncementDailyStatRepository
{
    private readonly NotificationDbContext _context;

    public AnnouncementDailyStatRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public Task IncrementAsync(Guid announcementId, DateOnly day, string eventType, CancellationToken ct = default)
    {
        var impressions = eventType == AnnouncementConstants.EventImpression ? 1 : 0;
        var dismissals = eventType == AnnouncementConstants.EventDismiss ? 1 : 0;
        var ctaClicks = eventType == AnnouncementConstants.EventCtaClick ? 1 : 0;
        var secondaryClicks = eventType == AnnouncementConstants.EventSecondaryClick ? 1 : 0;
        return _context.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO notification.announcement_daily_stats (announcement_id, day, impressions, dismissals, cta_clicks, secondary_clicks)
            VALUES ({announcementId}, {day}, {impressions}, {dismissals}, {ctaClicks}, {secondaryClicks})
            ON CONFLICT (announcement_id, day) DO UPDATE
            SET impressions = notification.announcement_daily_stats.impressions + EXCLUDED.impressions,
                dismissals = notification.announcement_daily_stats.dismissals + EXCLUDED.dismissals,
                cta_clicks = notification.announcement_daily_stats.cta_clicks + EXCLUDED.cta_clicks,
                secondary_clicks = notification.announcement_daily_stats.secondary_clicks + EXCLUDED.secondary_clicks", ct);
    }

    public async Task<IReadOnlyList<AnnouncementDailyStat>> ListSinceAsync(Guid announcementId, DateOnly since, CancellationToken ct = default) =>
        await _context.AnnouncementDailyStats
            .AsNoTracking()
            .Where(s => s.AnnouncementId == announcementId && s.Day >= since)
            .OrderBy(s => s.Day)
            .ToListAsync(ct);
}
