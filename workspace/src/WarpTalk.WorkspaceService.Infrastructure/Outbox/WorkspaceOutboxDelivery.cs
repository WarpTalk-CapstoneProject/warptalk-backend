using System.Text.Json;
using MassTransit;
using StackExchange.Redis;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Outbox;

public sealed class WorkspaceOutboxDelivery(
    IPublishEndpoint publishEndpoint,
    IConnectionMultiplexer redis)
{
    private const int StreamMaxLength = 10000;

    public async Task PublishAsync(
        WorkspaceOutboxMessage message,
        CancellationToken cancellationToken)
    {
        switch (message.EventType)
        {
            case WorkspaceEventTypes.WorkspaceCreated:
                await PublishAsync<WorkspaceCreatedEventPayload>(
                    message,
                    "workspace-events",
                    cancellationToken);
                break;
            case WorkspaceEventTypes.WorkspaceDeleted:
                await PublishAsync<WorkspaceDeletedEventPayload>(
                    message,
                    "workspace-events",
                    cancellationToken);
                break;
            case WorkspaceEventTypes.MemberRemoved:
                await PublishAsync<MemberRemovedEventPayload>(
                    message,
                    "workspace-events",
                    cancellationToken);
                break;
            case WorkspaceEventTypes.MemberRoleChanged:
                await PublishAsync<MemberRoleChangedEventPayload>(
                    message,
                    "workspace-events",
                    cancellationToken);
                break;
            case WorkspaceEventTypes.DocumentIngestionRequested:
                await PublishAsync<WorkspaceDocumentIngestionRequestedEventPayload>(
                    message,
                    "workspace-document-events",
                    cancellationToken);
                break;
            case WorkspaceEventTypes.DocumentInvalidated:
                await PublishAsync<WorkspaceDocumentInvalidatedEventPayload>(
                    message,
                    "workspace-document-events",
                    cancellationToken);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported workspace outbox event type '{message.EventType}'.");
        }
    }

    private async Task PublishAsync<TPayload>(
        WorkspaceOutboxMessage message,
        string redisStream,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<TPayload>(message.PayloadJson)
                      ?? throw new InvalidOperationException(
                          $"Workspace outbox payload for '{message.EventType}' is invalid.");
        var envelope = new EventEnvelope<TPayload>(
            message.Id,
            message.EventType,
            message.SchemaVersion,
            message.OccurredAt,
            message.Producer,
            message.CorrelationId,
            message.CausationId,
            message.WorkspaceId?.ToString(),
            payload);

        await publishEndpoint.Publish(envelope, cancellationToken);
        await PublishRedisCompatibilityEventAsync(
            redisStream,
            message,
            payload);
    }

    private async Task PublishRedisCompatibilityEventAsync<TPayload>(
        string stream,
        WorkspaceOutboxMessage message,
        TPayload payload)
    {
        var entries = new List<NameValueEntry>
        {
            new("event_id", message.Id.ToString()),
            new("schema_version", message.SchemaVersion.ToString()),
            new("event_type", message.CompatibilityEventType),
            new("contract_event_type", message.EventType),
            new("producer", message.Producer),
            new("correlation_id", message.CorrelationId ?? string.Empty),
            new("causation_id", message.CausationId ?? string.Empty),
            new("occurred_at", message.OccurredAt.ToString("O")),
            new("payload", message.PayloadJson)
        };

        AddCompatibilityFields(entries, payload);
        await redis.GetDatabase().StreamAddAsync(
            stream,
            entries.ToArray(),
            maxLength: StreamMaxLength,
            useApproximateMaxLength: true);
    }

    private static void AddCompatibilityFields<TPayload>(
        ICollection<NameValueEntry> entries,
        TPayload payload)
    {
        switch (payload)
        {
            case WorkspaceCreatedEventPayload created:
                entries.Add(new("workspace_id", created.WorkspaceId));
                entries.Add(new("name", created.Name));
                entries.Add(new("slug", created.Slug));
                entries.Add(new("owner_user_id", created.OwnerUserId));
                entries.Add(new("created_at", created.CreatedAt.ToString("O")));
                break;
            case WorkspaceDeletedEventPayload deleted:
                entries.Add(new("workspace_id", deleted.WorkspaceId));
                entries.Add(new("deleted_by", deleted.DeletedByUserId));
                entries.Add(new("deleted_at", deleted.DeletedAt.ToString("O")));
                break;
            case MemberRemovedEventPayload removed:
                entries.Add(new("workspace_id", removed.WorkspaceId));
                entries.Add(new("user_id", removed.UserId));
                entries.Add(new("removed_by", removed.RemovedByUserId));
                entries.Add(new("removed_at", removed.RemovedAt.ToString("O")));
                break;
            case MemberRoleChangedEventPayload roleChanged:
                entries.Add(new("workspace_id", roleChanged.WorkspaceId));
                entries.Add(new("target_user_id", roleChanged.TargetUserId));
                entries.Add(new("old_role", roleChanged.OldRole));
                entries.Add(new("new_role", roleChanged.NewRole));
                entries.Add(new("changed_by_user_id", roleChanged.ChangedByUserId));
                break;
            case WorkspaceDocumentIngestionRequestedEventPayload ingestion:
                entries.Add(new("document_id", ingestion.DocumentId));
                entries.Add(new("workspace_id", ingestion.WorkspaceId));
                entries.Add(new("storage_key", ingestion.StorageKey));
                entries.Add(new("file_name", ingestion.FileName));
                entries.Add(new("file_extension", ingestion.FileExtension));
                entries.Add(new("uploaded_by", ingestion.RequestedByUserId));
                entries.Add(new(
                    "confidentiality_level",
                    ingestion.IsSensitive ? "restricted" : "internal"));
                break;
            case WorkspaceDocumentInvalidatedEventPayload invalidated:
                entries.Add(new("document_id", invalidated.DocumentId));
                entries.Add(new("workspace_id", invalidated.WorkspaceId));
                entries.Add(new("reason", invalidated.Reason));
                break;
        }
    }
}
