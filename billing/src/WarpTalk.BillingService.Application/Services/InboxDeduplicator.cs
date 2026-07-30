using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public sealed class InboxDeduplicator(
    IUnitOfWork unitOfWork,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> TryAcceptAsync(
        Guid eventId,
        string consumer,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("Event id is required.", nameof(eventId));
        if (string.IsNullOrWhiteSpace(consumer))
            throw new ArgumentException("Consumer is required.", nameof(consumer));
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("Event type is required.", nameof(eventType));

        var exists = await unitOfWork.InboxMessages.AnyAsync(
            message => message.EventId == eventId && message.Consumer == consumer,
            cancellationToken);
        if (exists)
            return false;

        await unitOfWork.InboxMessages.AddAsync(new InboxMessage
        {
            EventId = eventId,
            Consumer = consumer,
            EventType = eventType,
            ProcessedAt = _timeProvider.GetUtcNow().UtcDateTime
        }, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
