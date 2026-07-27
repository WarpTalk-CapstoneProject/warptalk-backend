using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IEmbeddingIndexPublisher
{
    Task<string?> PublishEmbeddingIndexRequestAsync(
        WorkspaceDocument document,
        string fullText,
        bool externalLlmAllowed,
        CancellationToken ct = default);
}
