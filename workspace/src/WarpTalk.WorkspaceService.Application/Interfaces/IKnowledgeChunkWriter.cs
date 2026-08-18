using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Corrects or removes one indexed chunk, for a workspace Owner reviewing what the system
/// believes about their workspace.
///
/// WHY THIS IS A SEPARATE PORT FROM <see cref="IEmbeddingIndexPublisher"/>
///   Indexing is a fire-and-forget side effect on Redis because it needs an embedding model,
///   which lives in the Python workers and takes a network round trip. Neither operation here
///   needs one:
///
///     Deleting is bookkeeping — remove the point, no vector required.
///     Editing the FACT is bookkeeping too. The vector embeds the chunk's `text`; the fact is
///     an annotation stored beside it. Changing the annotation cannot make the vector wrong.
///
///   Since neither needs a model, neither needs to be asynchronous, and an Owner pressing
///   Delete should see the row gone rather than "it will disappear shortly". So this goes
///   straight to the store over the same port the listing already reads through.
///
/// WHY THE INDEXED TEXT IS NOT EDITABLE
///   It is the only field the vector was computed from. Rewriting it without re-embedding
///   leaves WarpBot retrieving on the old meaning and then showing the new words — a chunk
///   that lies about itself, and the failure is invisible until someone asks the question the
///   old vector answers. Editing text would have to travel the Redis path and come back, and
///   the honest thing to offer meanwhile is "fix the source, or delete this row".
/// </summary>
public interface IKnowledgeChunkWriter
{
    /// <summary>
    /// Merges the given annotation fields into the chunk's stored payload, leaving everything
    /// else — text, provenance, retention — exactly as it was.
    ///
    /// A null <see cref="KnowledgeChunkAnnotation.Fact"/> or
    /// <see cref="KnowledgeChunkAnnotation.FactCategory"/> clears that field rather than
    /// leaving the previous value: "this extracted fact is wrong" is a thing an Owner needs to
    /// be able to say, and it is not the same as saying nothing.
    /// </summary>
    Task SetAnnotationAsync(
        Guid workspaceId,
        string chunkId,
        KnowledgeChunkAnnotation annotation,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the chunk from the store.
    ///
    /// Implementations MUST NOT throw when the chunk is already absent. Two Owners on the same
    /// page pressing Delete is an ordinary race, and the second one's intent was satisfied.
    /// </summary>
    Task DeleteAsync(Guid workspaceId, string chunkId, CancellationToken ct = default);
}

/// <summary>What an Owner may change about a chunk. Everything else is the source's to say.</summary>
public record KnowledgeChunkAnnotation(string? Fact, string? FactCategory, bool AiRetrieval);
