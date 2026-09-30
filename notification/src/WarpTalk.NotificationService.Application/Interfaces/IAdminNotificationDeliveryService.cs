using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Application.Interfaces;

/// <summary>
/// The persistence half of admin announcement fan-out: what one delivery event writes, and what
/// the announcement's Status says about it afterwards. The stream plumbing (read, ack, retry,
/// dead-letter, realtime publish) stays in NotificationStreamConsumerService.
/// </summary>
public interface IAdminNotificationDeliveryService
{
    /// <summary>
    /// Writes one notification row per recipient in the chunk, the idempotency receipt, and the
    /// announcement's delivery counters in a single transaction. When this was the last chunk the
    /// announcement becomes Sent.
    /// </summary>
    /// <returns>The rows written, or an empty list when this event was already processed.</returns>
    Task<IReadOnlyList<NotificationMessage>> DeliverChunkAsync(
        DeliveryEventPayload payload,
        Guid eventId,
        CancellationToken ct = default);

    /// <summary>A delivery event was given up on: the announcement did not reach everyone.</summary>
    Task MarkDeliveryFailedAsync(Guid notificationId, CancellationToken ct = default);
}
