using System.Text.Json;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Outbox;

public sealed class WorkspaceOutboxWriter(IUnitOfWork unitOfWork)
{
    public Task EnqueueAsync<T>(
        EventEnvelope<T> envelope,
        string compatibilityEventType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compatibilityEventType);

        var message = new WorkspaceOutboxMessage
        {
            Id = envelope.EventId,
            EventType = envelope.EventType,
            CompatibilityEventType = compatibilityEventType,
            SchemaVersion = envelope.SchemaVersion,
            OccurredAt = envelope.OccurredAt,
            Producer = envelope.Producer,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            WorkspaceId = Guid.TryParse(envelope.WorkspaceId, out var workspaceId)
                ? workspaceId
                : null,
            PayloadJson = JsonSerializer.Serialize(envelope.Payload),
            AvailableAt = envelope.OccurredAt,
            CreatedAt = DateTime.UtcNow
        };

        return unitOfWork.WorkspaceOutboxMessageRepository
            .AddAsync(message, cancellationToken);
    }
}
