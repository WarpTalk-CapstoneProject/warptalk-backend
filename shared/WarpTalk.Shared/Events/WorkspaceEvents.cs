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
