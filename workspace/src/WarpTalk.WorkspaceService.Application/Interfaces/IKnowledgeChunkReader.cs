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

/// <summary>
/// Store-agnostic filter. Null/empty members mean "no constraint".
///
/// <paramref name="SourceTypes"/> is a list rather than one value because a single thing a
/// person names is not always one stored type: "glossary" is <c>glossary_term</c> AND
/// <c>global_glossary_term</c>, which are separate producers writing separate payloads. A
/// single-valued filter would force the caller to pick one and silently hide the other half.
///
/// <paramref name="ExcludedSourceTypes"/> is the inverse: source types the caller never wants
/// back, whatever else it asked for. It exists because raw meeting transcripts are indexed per
/// STT segment — one point per sentence spoken — and a workspace's whole indexed corpus is
/// otherwise dominated by them. They stay in the store (WarpBot answers detail questions from
/// them); they are simply not what "what does this workspace know" means to a person.
/// </summary>
public record KnowledgeChunkFilter(
    IReadOnlyList<string>? SourceTypes,
    string? FactCategory,
    IReadOnlyList<string>? ExcludedSourceTypes = null);

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
    bool AiRetrieval,
    /// <summary>
    /// Human-readable provenance for source types that are neither a document nor a
    /// transcript — a meeting's name on its summary, the term on a glossary entry. Trailing
    /// and defaulted so the positional construction in existing callers still compiles.
    /// </summary>
    string? SourceTitle = null);
