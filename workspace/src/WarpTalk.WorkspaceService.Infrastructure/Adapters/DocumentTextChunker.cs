using System;
using System.Collections.Generic;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Adapters;

public class DocumentTextChunker : IDocumentTextChunker
{
    public IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text) || chunkSize <= 0)
        {
            yield break;
        }

        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i)).Trim();
            if (chunk.Length > 0)
            {
                yield return chunk;
            }
        }
    }
}
