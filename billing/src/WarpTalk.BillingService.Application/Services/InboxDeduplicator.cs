using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
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
            throw new ArgumentException(BillingMessageConstants.ValidationMessages.EventIdRequired, nameof(eventId));
        if (string.IsNullOrWhiteSpace(consumer))
            throw new ArgumentException(BillingMessageConstants.ValidationMessages.ConsumerRequired, nameof(consumer));
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException(BillingMessageConstants.ValidationMessages.EventTypeRequired, nameof(eventType));

        var exists = await unitOfWork.InboxMessages.AnyAsync(
            message => message.EventId == eventId && message.Consumer == consumer,
            cancellationToken);
        if (exists)
            return false;

        var messageEntity = InboxMessageMapper.ToEntity(eventId, consumer, eventType, _timeProvider.GetUtcNow().UtcDateTime);
        await unitOfWork.InboxMessages.AddAsync(messageEntity, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
