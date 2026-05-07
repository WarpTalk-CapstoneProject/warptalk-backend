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
}
