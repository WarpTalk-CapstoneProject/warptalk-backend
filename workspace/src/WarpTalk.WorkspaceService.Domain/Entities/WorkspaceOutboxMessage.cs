namespace WarpTalk.WorkspaceService.Domain.Entities;

public sealed class WorkspaceOutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string CompatibilityEventType { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public DateTime OccurredAt { get; set; }
    public string Producer { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? LockedAt { get; set; }
    public DateTime? DeadLetteredAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}
