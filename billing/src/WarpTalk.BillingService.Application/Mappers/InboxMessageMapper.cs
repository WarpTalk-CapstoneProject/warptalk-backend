using System;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class InboxMessageMapper
{
    public static InboxMessage ToEntity(Guid eventId, string consumer, string eventType, DateTime processedAt)
    {
        return new InboxMessage
        {
            EventId = eventId,
            Consumer = consumer,
            EventType = eventType,
            ProcessedAt = processedAt
        };
    }
}
