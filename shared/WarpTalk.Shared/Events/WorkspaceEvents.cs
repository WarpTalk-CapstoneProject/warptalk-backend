namespace WarpTalk.Shared.Events;

/// <summary>
/// Event published when an entire Enterprise Workspace is soft-deleted or suspended.
/// Triggers cascading actions like force-terminating active meetings and revoking active streams.
/// </summary>
public record WorkspaceDeletedEvent
{
    public required string WorkspaceId { get; init; }
    public required string DeletedByUserId { get; init; }
    public required DateTime DeletedAt { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Event published when a specific Member is removed, suspended, or demoted from a Workspace.
/// Triggers realtime eviction (Kick) if the user is currently in a TranslationRoom.
/// </summary>
public record MemberRemovedEvent
{
    public required string WorkspaceId { get; init; }
    public required string UserId { get; init; }
    public required string RemovedByUserId { get; init; }
    public required DateTime RemovedAt { get; init; }
}

/// <summary>
/// Durable domain event emitted after a workspace document becomes AI-eligible.
/// Consumers can fan this into Redis Streams for realtime AI indexing jobs.
/// </summary>
public record WorkspaceDocumentIngestionRequestedEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public int SchemaVersion { get; init; } = 1;
    public required string DocumentId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string StorageKey { get; init; }
    public required string FileName { get; init; }
    public required string FileExtension { get; init; }
    public required string RequestedByUserId { get; init; }
    public bool IsSensitive { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Durable domain event emitted when a document must no longer be used by AI/RAG.
/// </summary>
public record WorkspaceDocumentInvalidatedEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public int SchemaVersion { get; init; } = 1;
    public required string DocumentId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Reason { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
