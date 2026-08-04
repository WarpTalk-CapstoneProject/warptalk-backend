using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

/// <summary>
/// Append-and-read access to the admin audit log (WT-210). There is deliberately no update or
/// delete method — the database grants match, so neither this interface nor the runtime role
/// can rewrite history.
/// </summary>
public interface IAdminAuditLogRepository
{
    Task AppendAsync(WorkspaceAdminAction entry, CancellationToken ct = default);

    /// <summary>True when this source has already recorded that correlated action.</summary>
    Task<bool> ExistsAsync(
        string sourceService,
        string? correlationId,
        string action,
        Guid? entityId,
        CancellationToken ct = default);

    Task<(List<WorkspaceAdminAction> Items, int TotalCount)> QueryAsync(
        AdminAuditLogFilter filter,
        CancellationToken ct = default);

    Task<List<WorkspaceAdminAction>> GetForEntityAsync(
        string entityType,
        Guid entityId,
        int limit,
        CancellationToken ct = default);
}

public sealed record AdminAuditLogFilter(
    int Page,
    int PageSize,
    Guid? ActorId,
    string? Action,
    string? EntityType,
    Guid? EntityId,
    Guid? WorkspaceId,
    string? SourceService,
    string? Result,
    DateTime? From,
    DateTime? To);
