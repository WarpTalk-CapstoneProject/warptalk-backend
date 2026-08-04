using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Models;

namespace WarpTalk.NotificationService.Application.Services;

/// <summary>
/// Makes the realtime notification channel durable for the bell notification center.
/// Producers that already persist before publishing are detected by notification id,
/// while direct publishers are stored exactly once.
/// </summary>
public sealed class RealtimeNotificationPersistenceHandler(IUnitOfWork unitOfWork)
{
    public async Task<bool> HandleAsync(
        RealtimeNotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(message.Id, out var notificationId)
            || !Guid.TryParse(message.UserId, out var userId))
        {
            return false;
        }

        var repository = unitOfWork.Repository<NotificationMessage>();
        var existing = await repository.FindAsync(notification =>
            notification.Id == notificationId && notification.UserId == userId);
        if (existing.Any())
        {
            return false;
        }

        var createdAt = DateTime.TryParse(
            message.CreatedAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsedCreatedAt)
                ? parsedCreatedAt.ToUniversalTime()
                : DateTime.UtcNow;

        await repository.AddAsync(new NotificationMessage
        {
            Id = notificationId,
            UserId = userId,
            Type = message.Type,
            Title = message.Title,
            Content = message.Content,
            ActionUrl = message.ActionUrl,
            PayloadJson = string.IsNullOrWhiteSpace(message.PayloadJson) ? "{}" : message.PayloadJson,
            IsRead = false,
            CreatedAt = createdAt
        });
        await unitOfWork.SaveChangesAsync();
        return true;
    }
}
