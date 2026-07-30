using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Models;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IAdminNotificationRepository
{
    Task AddAsync(AdminNotification entity, CancellationToken ct = default);
    Task<AdminNotification?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<(IEnumerable<AdminNotification> Items, int TotalCount)> GetPaginatedAsync(AdminNotificationFilter filter, CancellationToken ct = default);
}
