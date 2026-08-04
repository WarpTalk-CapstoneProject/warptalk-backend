using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>
/// Transactional outbox for durable Workspace domain-event delivery.
/// </summary>
public partial class OutboxMessage
{
    public Guid Id { get; set; }

    public string EventType { get; set; } = null!;

    public string CompatibilityEventType { get; set; } = null!;

    public int SchemaVersion { get; set; }

    public DateTime OccurredAt { get; set; }

    public string Producer { get; set; } = null!;

    public string? CorrelationId { get; set; }

    public string? CausationId { get; set; }

    public Guid? WorkspaceId { get; set; }

    public string PayloadJson { get; set; } = null!;

    public int AttemptCount { get; set; }

    public DateTime AvailableAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? LockedAt { get; set; }

    public DateTime? DeadLetteredAt { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }
}
