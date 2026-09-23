using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Mappers;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;

namespace WarpTalk.NotificationService.Application.Services;

/// <summary>
/// Status used to be written exactly once, as "Pending" at creation, and never again: the
/// consumer wrote every recipient's row and acknowledged the event without touching the
/// announcement, and a dead-lettered event left it untouched too. So the admin list showed
/// "Pending" for announcements that had reached everybody and for ones that had reached nobody.
/// </summary>
public class AdminNotificationDeliveryService : IAdminNotificationDeliveryService
{
    public const string InboxConsumerName = "admin-notification-delivery@v1";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<AdminNotificationDeliveryService> _logger;

    public AdminNotificationDeliveryService(
        IUnitOfWork unitOfWork,
        ILogger<AdminNotificationDeliveryService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NotificationMessage>> DeliverChunkAsync(
        DeliveryEventPayload payload,
        Guid eventId,
        CancellationToken ct = default)
    {
        // WT-699 / TC4104: every mode arrives with its recipients already resolved to ids by
        // AdminNotificationService, so the ids — not the mode — decide what is deliverable.
        if (payload.TargetAudienceMode is not (NotificationConstants.TargetModeSpecificUsers
                or NotificationConstants.TargetModeBroadcast
                or NotificationConstants.TargetModeSegment)
            || payload.SpecificUserIds is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"Unsupported admin notification audience mode '{payload.TargetAudienceMode}'.");
        }

        var inboxRepository = _unitOfWork.NotificationInboxMessageRepository;
        if (await inboxRepository.HasProcessedAsync(eventId, InboxConsumerName, ct))
            return Array.Empty<NotificationMessage>();

        var adminNotification = await _unitOfWork.AdminNotificationRepository
            .GetByIdAsync(payload.NotificationId, ct)
            ?? throw new KeyNotFoundException(
                $"Admin notification {payload.NotificationId} does not exist.");

        var messages = payload.SpecificUserIds
            .Distinct()
            .Select(userId => NotificationMessageMapper.ToEntity(adminNotification, userId))
            .ToArray();

        // The receipt, the rows and the counters commit together. If the receipt insert loses a
        // race with another replica that reclaimed the same event, everything rolls back and the
        // retry finds the receipt — so a chunk is never counted twice.
        await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            await _unitOfWork.NotificationMessageRepository.AddRangeAsync(messages);
            await inboxRepository.AddAsync(new NotificationInboxMessage
            {
                EventId = eventId,
                Consumer = InboxConsumerName,
                EventType = "admin.notification.delivery@v1",
                ProcessedAt = DateTime.UtcNow
            }, ct);
            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.AdminNotificationRepository
                .RecordChunkDeliveredAsync(adminNotification.Id, messages.Length, ct);
            await _unitOfWork.CommitTransactionAsync(ct);
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync(CancellationToken.None);
            throw;
        }

        return messages;
    }

    public async Task MarkDeliveryFailedAsync(Guid notificationId, CancellationToken ct = default)
    {
        if (await _unitOfWork.AdminNotificationRepository.MarkFailedAsync(notificationId, ct))
        {
            _logger.LogError(
                "Admin notification {NotificationId} marked {Status}: a delivery event was dead-lettered.",
                notificationId,
                NotificationConstants.StatusFailed);
        }
    }
}
