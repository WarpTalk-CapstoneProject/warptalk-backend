using MassTransit;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Events;

namespace WarpTalk.BillingService.Infrastructure.Messaging;

public sealed class MassTransitOutboxEventPublisher(IPublishEndpoint publishEndpoint) : IOutboxEventPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        => publishEndpoint.Publish(
            new OutboxEventMessage
            {
                EventId = message.Id,
                EventType = message.EventType,
                SchemaVersion = message.SchemaVersion,
                OccurredAt = message.OccurredAt,
                Producer = message.Producer,
                CorrelationId = message.CorrelationId,
                CausationId = message.CausationId,
                WorkspaceId = message.WorkspaceId,
                PayloadJson = message.PayloadJson
            },
            context =>
            {
                context.MessageId = message.Id;
                context.CorrelationId = Guid.TryParse(message.CorrelationId, out var correlationId)
                    ? correlationId
                    : null;
                context.Headers.Set("x-warptalk-event-type", message.EventType);
                context.Headers.Set("x-warptalk-schema-version", message.SchemaVersion);
            },
            cancellationToken);
}
