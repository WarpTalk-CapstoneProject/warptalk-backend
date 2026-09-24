using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class NotificationTemplateRepository : GenericRepository<NotificationTemplate>, INotificationTemplateRepository
{
    public NotificationTemplateRepository(NotificationDbContext context) : base(context)
    {
    }

    public Task<NotificationTemplate?> GetByTypeAsync(string type, string channel, CancellationToken ct = default) =>
        _dbSet.FirstOrDefaultAsync(t => t.Type == type && t.Channel == channel, ct);

    public Task<NotificationTemplate?> GetActiveByTypeAsync(string type, string channel, CancellationToken ct = default) =>
        _dbSet.AsNoTracking().FirstOrDefaultAsync(t => t.Type == type && t.Channel == channel && t.IsActive, ct);

    public async Task<IReadOnlyList<NotificationTemplate>> ListByChannelAsync(string channel, CancellationToken ct = default) =>
        await _dbSet.AsNoTracking().Where(t => t.Channel == channel).ToListAsync(ct);
}
