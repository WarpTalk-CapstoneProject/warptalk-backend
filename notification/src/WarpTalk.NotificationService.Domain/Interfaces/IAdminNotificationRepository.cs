using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Models;

namespace WarpTalk.NotificationService.Domain.Interfaces;

/// <summary>
/// Admin broadcast notifications.
///
/// The cancellable overloads are kept alongside the inherited generic ones on purpose: this
/// service's IGenericRepository takes no CancellationToken, and dropping these in favour of it
/// would take cancellation away from callers that already pass one.
/// </summary>
public interface IAdminNotificationRepository : IGenericRepository<AdminNotification>
{
    Task AddAsync(AdminNotification entity, CancellationToken ct);
    Task<AdminNotification?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<(IEnumerable<AdminNotification> Items, int TotalCount)> GetPaginatedAsync(AdminNotificationFilter filter, CancellationToken ct = default);
}
