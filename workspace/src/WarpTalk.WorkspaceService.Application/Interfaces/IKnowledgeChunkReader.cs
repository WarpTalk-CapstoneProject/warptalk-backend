using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Reads indexed chunks back out of the vector store.
///
/// This is the read counterpart to <see cref="IEmbeddingIndexPublisher"/>. Indexing is a
/// fire-and-forget side effect and travels over Redis; this is a synchronous, paginated read
/// that a user is waiting on, so it goes straight to the store rather than round-tripping
/// through a worker that would have to reply on a channel and time out.
///
/// The port lives in Application so the service layer never learns which store is behind it.
/// </summary>
public interface IKnowledgeChunkReader
{
    /// <summary>
    /// Returns one page of chunks belonging to <paramref name="workspaceId"/>.
    ///
    /// Implementations MUST return an empty page — never throw — when the workspace has
    /// nothing indexed. A workspace that has never uploaded a document is a normal state,
    /// not a failure, and the caller renders it as an empty table.
    /// </summary>
    /// <param name="cursor">Continuation token from a previous call, or null for the first page.</param>
    Task<KnowledgeChunkPage> ScrollAsync(
        Guid workspaceId,
        KnowledgeChunkFilter filter,
        int limit,
        string? cursor,
        CancellationToken ct = default);
}

/// <summary>Store-agnostic filter. Null members mean "no constraint".</summary>
public record KnowledgeChunkFilter(string? SourceType, string? FactCategory);

/// <summary>One page of raw chunk records. <paramref name="NextCursor"/> is null on the last page.</summary>
public record KnowledgeChunkPage(IReadOnlyList<KnowledgeChunkRecord> Items, string? NextCursor);

/// <summary>
/// A chunk as the store holds it. Deliberately close to the stored payload — mapping to the
/// API shape is the service's job, not the adapter's.
/// </summary>
public record KnowledgeChunkRecord(
    string ChunkId,
    string SourceType,
    string? Text,
    string? Fact,
    string? FactCategory,
    string? DocumentId,
    string? DocumentName,
    int? ChunkIndex,
    string? SpeakerName,
    long? StartMs,
    string? RetentionState,
    string? DeletionState,
    bool AiRetrieval);
