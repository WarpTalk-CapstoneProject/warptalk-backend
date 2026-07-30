using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Models;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class AdminNotificationRepository : IAdminNotificationRepository
{
    private readonly NotificationDbContext _context;

    public AdminNotificationRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(AdminNotification entity, CancellationToken ct = default)
    {
        await _context.AdminNotifications.AddAsync(entity, ct);
    }

    public async Task<AdminNotification?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.AdminNotifications.FindAsync(new object[] { id }, ct);
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
