using System.Text.Json;
using MassTransit;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Outbox;

namespace WarpTalk.WorkspaceService.Tests;

public sealed class WorkspaceOutboxDeliveryTests
{
    [Fact]
    public async Task PublishAsync_UsesStableEventIdAcrossRabbitMqAndRedis()
    {
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var delivery = new WorkspaceOutboxDelivery(publishEndpoint, redis);
        var eventId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var payload = new WorkspaceDocumentInvalidatedEventPayload(
            Guid.NewGuid().ToString(),
            workspaceId.ToString(),
            "deleted");
        var message = new WorkspaceOutboxMessage
        {
            Id = eventId,
            EventType = WorkspaceEventTypes.DocumentInvalidated,
            CompatibilityEventType = "DocumentDeleted",
            SchemaVersion = 1,
            OccurredAt = DateTime.UtcNow,
            Producer = WorkspaceEventTypes.Producer,
            WorkspaceId = workspaceId,
            PayloadJson = JsonSerializer.Serialize(payload)
        };

        await delivery.PublishAsync(message, CancellationToken.None);

        await publishEndpoint.Received(1).Publish(
            Arg.Is<EventEnvelope<WorkspaceDocumentInvalidatedEventPayload>>(envelope =>
                envelope.EventId == eventId
                && envelope.Payload.DocumentId == payload.DocumentId),
            Arg.Any<CancellationToken>());
        await database.Received(1).StreamAddAsync(
            "workspace-document-events",
            Arg.Is<NameValueEntry[]>(entries =>
                Contains(entries, "event_id", eventId.ToString())
                && Contains(entries, "contract_event_type", WorkspaceEventTypes.DocumentInvalidated)
                && Contains(entries, "event_type", "DocumentDeleted")),
            maxLength: 10000,
            useApproximateMaxLength: true);
    }

    private static bool Contains(
        IEnumerable<NameValueEntry> entries,
        string name,
        string value) =>
        entries.Any(entry => entry.Name == name && entry.Value == value);
}
