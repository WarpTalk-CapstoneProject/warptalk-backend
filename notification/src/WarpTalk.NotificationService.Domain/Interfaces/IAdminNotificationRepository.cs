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

    /// <summary>
    /// Counts one processed delivery chunk and its recipients, and flips a Pending announcement
    /// to Sent (stamping SentAt) when that was the last chunk. One UPDATE statement, so replicas
    /// finishing different chunks of the same announcement cannot lose each other's increment.
    /// Runs immediately — call it inside the transaction that writes the chunk's rows.
    /// </summary>
    Task RecordChunkDeliveredAsync(Guid id, int recipientCount, CancellationToken ct);

    /// <summary>
    /// Marks a still-Pending announcement Failed. A Sent announcement is left alone.
    /// Returns false when no Pending row matched.
    /// </summary>
    Task<bool> MarkFailedAsync(Guid id, CancellationToken ct);
}
