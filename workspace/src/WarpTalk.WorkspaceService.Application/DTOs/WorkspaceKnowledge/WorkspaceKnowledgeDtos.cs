using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceKnowledge;

/// <summary>
/// One indexed chunk as a person can read it: the text that was embedded, the one fact
/// extracted from it, and where it came from.
///
/// Fields are nullable because the two source types carry different provenance — a document
/// chunk has a name and an index, a transcript chunk has a speaker and an offset — and
/// because chunks indexed before the payload carried text or facts still exist and must be
/// listed honestly rather than hidden or faked.
/// </summary>
public record WorkspaceKnowledgeChunkDto(
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
    /// The meeting's name on a summary row, the term on a glossary row. Null for documents,
    /// which carry <see cref="DocumentName"/> instead.
    /// </summary>
    string? SourceTitle,
    /// <summary>
    /// When this chunk was indexed, in epoch milliseconds, or null for a row stored before the
    /// indexer stamped one. The listing orders by it, newest first, and puts undated rows last.
    /// </summary>
    long? IndexedAtMs
);

/// <summary>
/// A page of chunks plus the cursor for the next one.
///
/// Cursor rather than page number because the vector store pages by continuation token, and
/// an offset-based API on top of it would have to scan from the start for every page and
/// would silently skip or repeat rows when the collection changes mid-listing.
/// <paramref name="NextCursor"/> is null on the last page.
/// </summary>
public record WorkspaceKnowledgePageDto(
    List<WorkspaceKnowledgeChunkDto> Items,
    string? NextCursor
);

/// <summary>
/// Query for the knowledge listing. All filters are optional; an empty query lists everything
/// in the workspace, newest page first as the store returns it.
/// </summary>
public class GetWorkspaceKnowledgeQuery
{
    /// <summary>"document" or "transcript". Null lists both.</summary>
    public string? SourceType { get; set; }

    /// <summary>One of the six closed fact categories. Null lists all.</summary>
    public string? FactCategory { get; set; }

    /// <summary>Continuation token from the previous page's NextCursor.</summary>
    public string? Cursor { get; set; }

    public int PageSize { get; set; } = 50;
}

/// <summary>
/// What an Owner may correct about an indexed chunk.
///
/// Deliberately three fields and not the whole DTO. The chunk's TEXT is the only thing the
/// vector was computed from, so editing it here would leave WarpBot retrieving on the old
/// meaning and displaying the new words — and the provenance (which document, which meeting,
/// which chunk index) is a record of where the text came from, which is not an opinion anyone
/// gets to revise.
///
/// Both string fields are nullable and both nulls are meaningful: clearing a wrong fact is a
/// correction in its own right, and is not the same as declining to change it.
/// </summary>
public class UpdateWorkspaceKnowledgeChunkRequest
{
    /// <summary>The one-line fact, or null to say this chunk has none.</summary>
    public string? Fact { get; set; }

    /// <summary>One of the six closed categories, or null. Rejected if it is neither.</summary>
    public string? FactCategory { get; set; }

    /// <summary>
    /// Whether WarpBot may retrieve this chunk when answering. Turning it off is the softer
    /// alternative to deleting: the row stays visible and auditable on this page, and stops
    /// being reachable in an answer.
    /// </summary>
    public bool AiRetrieval { get; set; } = true;
}
