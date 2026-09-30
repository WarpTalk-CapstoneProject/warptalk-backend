using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Models;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class AdminNotificationRepository
    : GenericRepository<AdminNotification>, IAdminNotificationRepository
{
    public AdminNotificationRepository(NotificationDbContext context) : base(context)
    {
    }

    public async Task AddAsync(AdminNotification entity, CancellationToken ct)
    {
        await _context.AdminNotifications.AddAsync(entity, ct);
    }

    public async Task<AdminNotification?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await _context.AdminNotifications.FindAsync(new object[] { id }, ct);
    }

    public async Task RecordChunkDeliveredAsync(Guid id, int recipientCount, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Every right-hand side reads the row as it was before this UPDATE, so "+ 1 >= total" is
        // "this chunk is the last one". A Failed announcement keeps its status: some chunk already
        // went to the dead-letter stream, and finishing the others does not make it whole.
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteUpdateAsync(
            System.Linq.Queryable.Where(_context.AdminNotifications, n => n.Id == id),
            s => s
                .SetProperty(n => n.DeliveredChunkCount, n => n.DeliveredChunkCount + 1)
                .SetProperty(n => n.DeliveredCount, n => n.DeliveredCount + recipientCount)
                .SetProperty(
                    n => n.Status,
                    n => n.Status == NotificationConstants.StatusPending
                         && n.DeliveredChunkCount + 1 >= n.DeliveryChunkCount
                        ? NotificationConstants.StatusSent
                        : n.Status)
                .SetProperty(
                    n => n.SentAt,
                    n => n.Status == NotificationConstants.StatusPending
                         && n.DeliveredChunkCount + 1 >= n.DeliveryChunkCount
                        ? (DateTime?)now
                        : n.SentAt)
                .SetProperty(n => n.UpdatedAt, now),
            ct);
    }

    public async Task<bool> MarkFailedAsync(Guid id, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var updated = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteUpdateAsync(
            System.Linq.Queryable.Where(
                _context.AdminNotifications,
                n => n.Id == id && n.Status == NotificationConstants.StatusPending),
            s => s
                .SetProperty(n => n.Status, NotificationConstants.StatusFailed)
                .SetProperty(n => n.UpdatedAt, now),
            ct);
        return updated > 0;
    }

    public async Task<(IEnumerable<AdminNotification> Items, int TotalCount)> GetPaginatedAsync(
        AdminNotificationFilter filter,
        CancellationToken ct = default)
    {
        var query = _context.AdminNotifications.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Title))
        {
            var titleLower = filter.Title.ToLower();
            query = query.Where(n => n.Title.ToLower().Contains(titleLower));
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            query = query.Where(n => n.Type == filter.Type);
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = query.Where(n => n.Status == filter.Status);
        }

        if (filter.CreatedFrom.HasValue)
        {
            query = query.Where(n => n.CreatedAt >= filter.CreatedFrom.Value);
        }

        if (filter.CreatedTo.HasValue)
        {
            query = query.Where(n => n.CreatedAt <= filter.CreatedTo.Value);
        }

        var totalCount = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query, ct);

        var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            System.Linq.Queryable.Take(
                System.Linq.Queryable.Skip(
                    System.Linq.Queryable.OrderByDescending(query, n => n.CreatedAt),
                    (filter.Page - 1) * filter.PageSize
                ),
                filter.PageSize
            ), ct);

        return (items, totalCount);
    }
}
