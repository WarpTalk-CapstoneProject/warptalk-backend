using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceDocumentEventPublisher
{
    Task PublishDocumentUploadedAsync(Guid documentId, Guid workspaceId, string storageKey, string fileName, string fileExtension, Guid userId, string? confidentialityLevel = null, CancellationToken ct = default);
    Task PublishDocumentDeletedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default);
    Task PublishDocumentArchivedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default);
    Task PublishDocumentLifecycleAsync(
        Guid documentId,
        Guid workspaceId,
        string status,
        string ingestionStatus,
        string eventType,
        DateTime updatedAt,
        Guid? userId = null,
        CancellationToken ct = default);
    Task PublishEmbeddingIndexRequestAsync(Guid documentId, Guid workspaceId, string fullText, bool externalLlmAllowed, CancellationToken ct = default);
}
