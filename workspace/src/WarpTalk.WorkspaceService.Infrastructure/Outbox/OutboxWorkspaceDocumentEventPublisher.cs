using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Infrastructure.Clients;

namespace WarpTalk.WorkspaceService.Infrastructure.Outbox;

public sealed class OutboxWorkspaceDocumentEventPublisher(
    WorkspaceOutboxWriter writer,
    WorkspaceDocumentAuxiliaryPublisher auxiliaryPublisher) : IWorkspaceDocumentEventPublisher
{
    public Task PublishDocumentUploadedAsync(
        Guid documentId,
        Guid workspaceId,
        string storageKey,
        string fileName,
        string fileExtension,
        Guid userId,
        string? confidentialityLevel = null,
        CancellationToken ct = default)
    {
        var level = confidentialityLevel
                    ?? WorkspaceDocumentConstants.NonSensitiveConfidentialityLevel;
        var envelope = DomainEventEnvelope.Create(
            WorkspaceEventTypes.DocumentIngestionRequested,
            WorkspaceEventTypes.Producer,
            workspaceId.ToString(),
            new WorkspaceDocumentIngestionRequestedEventPayload(
                documentId.ToString(),
                workspaceId.ToString(),
                storageKey,
                fileName,
                fileExtension,
                userId.ToString(),
                string.Equals(
                    level,
                    WorkspaceDocumentConstants.SensitiveConfidentialityLevel,
                    StringComparison.OrdinalIgnoreCase)));
        return writer.EnqueueAsync(envelope, "DocumentUploaded", ct);
    }

    public Task PublishDocumentDeletedAsync(
        Guid documentId,
        Guid workspaceId,
        CancellationToken ct = default) =>
        EnqueueInvalidationAsync(documentId, workspaceId, "deleted", "DocumentDeleted", ct);

    public Task PublishDocumentArchivedAsync(
        Guid documentId,
        Guid workspaceId,
        CancellationToken ct = default) =>
        EnqueueInvalidationAsync(documentId, workspaceId, "archived", "DocumentArchived", ct);

    public Task PublishDocumentLifecycleAsync(
        Guid documentId,
        Guid workspaceId,
        string status,
        string ingestionStatus,
        string eventType,
        DateTime updatedAt,
        Guid? userId = null,
        CancellationToken ct = default) =>
        auxiliaryPublisher.PublishDocumentLifecycleAsync(
            documentId,
            workspaceId,
            status,
            ingestionStatus,
            eventType,
            updatedAt,
            userId,
            ct);

    public Task PublishEmbeddingIndexRequestAsync(
        Guid documentId,
        Guid workspaceId,
        string fullText,
        bool externalLlmAllowed,
        CancellationToken ct = default) =>
        auxiliaryPublisher.PublishEmbeddingIndexRequestAsync(
            documentId,
            workspaceId,
            fullText,
            externalLlmAllowed,
            ct);

    private Task EnqueueInvalidationAsync(
        Guid documentId,
        Guid workspaceId,
        string reason,
        string compatibilityEventType,
        CancellationToken ct)
    {
        var envelope = DomainEventEnvelope.Create(
            WorkspaceEventTypes.DocumentInvalidated,
            WorkspaceEventTypes.Producer,
            workspaceId.ToString(),
            new WorkspaceDocumentInvalidatedEventPayload(
                documentId.ToString(),
                workspaceId.ToString(),
                reason));
        return writer.EnqueueAsync(envelope, compatibilityEventType, ct);
    }
}
