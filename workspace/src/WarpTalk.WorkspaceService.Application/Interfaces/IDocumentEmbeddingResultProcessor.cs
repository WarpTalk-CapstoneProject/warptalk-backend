using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IDocumentEmbeddingResultProcessor
{
    Task ProcessResultAsync(Dictionary<string, string> values, CancellationToken ct = default);
}
