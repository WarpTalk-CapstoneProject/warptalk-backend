using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IDocumentTextChunker
{
    IEnumerable<string> ChunkText(string text, int chunkSize);
}
