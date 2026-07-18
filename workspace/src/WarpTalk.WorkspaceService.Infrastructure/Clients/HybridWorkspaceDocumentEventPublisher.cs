using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public class HybridWorkspaceDocumentEventPublisher : IWorkspaceDocumentEventPublisher
{
    private const string RedisStreamName = "workspace-document-events";

    private readonly IConnectionMultiplexer _redis;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<HybridWorkspaceDocumentEventPublisher> _logger;

    public HybridWorkspaceDocumentEventPublisher(
        IConnectionMultiplexer redis,
        IPublishEndpoint publishEndpoint,
        ILogger<HybridWorkspaceDocumentEventPublisher> logger)
    {
        _redis = redis;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task PublishDocumentUploadedAsync(
        Guid documentId,
        Guid workspaceId,
        string storageKey,
        string fileName,
        string fileExtension,
        Guid userId,
        bool isSensitive,
        CancellationToken ct = default)
    {
        var message = new WorkspaceDocumentIngestionRequestedEvent
        {
            DocumentId = documentId.ToString(),
            WorkspaceId = workspaceId.ToString(),
            StorageKey = storageKey,
            FileName = fileName,
            FileExtension = fileExtension,
            RequestedByUserId = userId.ToString(),
            IsSensitive = isSensitive,
        };

        await PublishRabbitMqEventAsync(message, documentId, ct);
        await PublishRedisCompatibilityEventAsync("DocumentUploaded", documentId, workspaceId, message.EventId, message.OccurredAt, entries =>
        {
            entries.Add(new NameValueEntry("storage_key", storageKey));
            entries.Add(new NameValueEntry("file_name", fileName));
            entries.Add(new NameValueEntry("file_extension", fileExtension));
            entries.Add(new NameValueEntry("uploaded_by", userId.ToString()));
            entries.Add(new NameValueEntry("is_sensitive", isSensitive.ToString()));
        });
    }

    public async Task PublishDocumentDeletedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default)
    {
        await PublishInvalidationAsync(documentId, workspaceId, "deleted", "DocumentDeleted", ct);
    }

    public async Task PublishDocumentArchivedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default)
    {
        await PublishInvalidationAsync(documentId, workspaceId, "archived", "DocumentArchived", ct);
    }

    private async Task PublishInvalidationAsync(
        Guid documentId,
        Guid workspaceId,
        string reason,
        string redisEventType,
        CancellationToken ct)
    {
        var message = new WorkspaceDocumentInvalidatedEvent
        {
            DocumentId = documentId.ToString(),
            WorkspaceId = workspaceId.ToString(),
            Reason = reason,
        };

        await PublishRabbitMqEventAsync(message, documentId, ct);
        await PublishRedisCompatibilityEventAsync(redisEventType, documentId, workspaceId, message.EventId, message.OccurredAt);
    }

    private async Task PublishRabbitMqEventAsync<T>(T message, Guid documentId, CancellationToken ct)
        where T : class
    {
        try
        {
            await _publishEndpoint.Publish(message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish document domain event to RabbitMQ. MessageType: {MessageType}, DocumentId: {DocumentId}",
                typeof(T).Name,
                documentId);
        }
    }

    private async Task PublishRedisCompatibilityEventAsync(
        string eventType,
        Guid documentId,
        Guid workspaceId,
        Guid eventId,
        DateTime occurredAt,
        Action<List<NameValueEntry>>? configure = null)
    {
        try
        {
            var entries = new List<NameValueEntry>
            {
                new("event_id", eventId.ToString()),
                new("schema_version", "1"),
                new("event_type", eventType),
                new("document_id", documentId.ToString()),
                new("workspace_id", workspaceId.ToString()),
                new("occurred_at", occurredAt.ToString("O")),
            };
            configure?.Invoke(entries);

            var db = _redis.GetDatabase();
            await db.StreamAddAsync(RedisStreamName, entries.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish document compatibility event to Redis Stream. EventType: {EventType}, DocumentId: {DocumentId}",
                eventType,
                documentId);
        }
    }
}
