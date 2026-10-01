using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class EmailDeliveryStatRepository : IEmailDeliveryStatRepository
{
    private readonly NotificationDbContext _context;

    public EmailDeliveryStatRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public Task IncrementAsync(string templateKey, string locale, DateOnly day, bool succeeded, CancellationToken ct = default)
    {
        var sent = succeeded ? 1 : 0;
        var failed = succeeded ? 0 : 1;
        // One statement, so senders on different replicas counting the same day cannot lose each
        // other's increment.
        return _context.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO notification.email_delivery_stats (template_key, locale, day, sent_count, failed_count)
            VALUES ({templateKey}, {locale}, {day}, {sent}, {failed})
            ON CONFLICT (template_key, locale, day) DO UPDATE
            SET sent_count = notification.email_delivery_stats.sent_count + EXCLUDED.sent_count,
                failed_count = notification.email_delivery_stats.failed_count + EXCLUDED.failed_count", ct);
    }

    public async Task<IReadOnlyList<EmailDeliveryStat>> ListSinceAsync(string? templateKey, DateOnly since, CancellationToken ct = default)
    {
        var query = _context.EmailDeliveryStats.AsNoTracking().Where(s => s.Day >= since);
        if (!string.IsNullOrWhiteSpace(templateKey)) query = query.Where(s => s.TemplateKey == templateKey);
        return await query.OrderBy(s => s.Day).ToListAsync(ct);
    }
}
