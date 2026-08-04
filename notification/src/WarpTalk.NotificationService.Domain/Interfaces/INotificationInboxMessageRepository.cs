using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

/// <summary>
/// Idempotency receipts for events this service consumes.
///
/// Deliberately not built on IGenericRepository: NotificationInboxMessage is keyed by
/// (EventId, Consumer), so the generic GetByIdAsync(Guid) has no meaning here and would fail at
/// runtime. The two operations below are the only ones the consumers actually perform.
/// </summary>
public interface INotificationInboxMessageRepository
{
    /// <summary>True when this consumer has already handled that event.</summary>
    Task<bool> HasProcessedAsync(Guid eventId, string consumer, CancellationToken ct = default);

    Task AddAsync(NotificationInboxMessage receipt, CancellationToken ct = default);
}
