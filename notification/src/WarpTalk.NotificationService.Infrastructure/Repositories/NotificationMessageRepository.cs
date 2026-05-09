using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class NotificationMessageRepository : GenericRepository<NotificationMessage>, INotificationMessageRepository
{
    public NotificationMessageRepository(NotificationDbContext context) : base(context) { }

    public async Task<(IEnumerable<NotificationMessage> Items, int TotalCount)> GetPaginatedByUserIdAsync(Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        var count = await CountAsync(n => n.UserId == userId);
        var items = await FindWithPaginationAsync(
            n => n.UserId == userId, 
            (page - 1) * pageSize, 
            pageSize, 
            q => q.OrderByDescending(n => n.CreatedAt)
        );
        return (items, count);
    }

    public async Task<NotificationMessage?> GetByIdAndUserIdAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        return (await FindAsync(n => n.Id == id && n.UserId == userId)).FirstOrDefault();
    }

    public async Task<IEnumerable<NotificationMessage>> GetUnreadByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await FindAsync(n => n.UserId == userId && !n.IsRead);
    }

    public async Task<bool> MarkAsReadAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var rows = await _dbSet
            .Where(n => n.Id == id && n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);
                
        return rows > 0;
    }

    public async Task<int> MarkAllAsReadAsync(Guid userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);
    }
}
