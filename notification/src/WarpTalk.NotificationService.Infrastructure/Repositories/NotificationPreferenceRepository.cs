using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class NotificationPreferenceRepository : GenericRepository<NotificationPreference>, INotificationPreferenceRepository
{
    public NotificationPreferenceRepository(NotificationDbContext context) : base(context)
    {
    }

    public async Task<NotificationPreference?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task<IReadOnlySet<Guid>> ListEmailOptOutsAsync(
        IReadOnlyCollection<Guid> userIds, string notificationType, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return new HashSet<Guid>();
        var ids = userIds.ToList();
        var optedOut = await _dbSet
            .AsNoTracking()
            .Where(p => ids.Contains(p.UserId) && p.NotificationType == notificationType && !p.EmailEnabled)
            .Select(p => p.UserId)
            .ToListAsync(ct);
        return optedOut.ToHashSet();
    }
}
