using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceDocumentEventPublisher
{
    Task PublishDocumentUploadedAsync(Guid documentId, Guid workspaceId, string storageKey, string fileName, string fileExtension, Guid userId, bool isSensitive, CancellationToken ct = default);
    Task PublishDocumentDeletedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default);
    Task PublishDocumentArchivedAsync(Guid documentId, Guid workspaceId, CancellationToken ct = default);
    Task PublishEmbeddingIndexRequestAsync(Guid documentId, Guid workspaceId, string fullText, bool externalLlmAllowed, CancellationToken ct = default);
}
