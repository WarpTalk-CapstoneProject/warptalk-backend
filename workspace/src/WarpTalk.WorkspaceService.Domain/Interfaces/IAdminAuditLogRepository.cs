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

    /// <summary>
    /// Keyset read for the platform audit screen, newest first by (performed_at, id). Returns up to
    /// <see cref="AdminAuditLogSearch.Take"/> rows strictly after the cursor. A <c>succeeded</c> row
    /// whose own <c>{correlation}:failed</c> entry exists is left out: it was recorded before a
    /// commit that then failed, and the failed entry is the account of that attempt.
    /// </summary>
    Task<List<WorkspaceAdminAction>> SearchAsync(AdminAuditLogSearch search, CancellationToken ct = default);

    Task<WorkspaceAdminAction?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Distinct actions, entity types, sources and actors present in the store, with counts.</summary>
    Task<AdminAuditLogFacetRows> GetFacetsAsync(CancellationToken ct = default);

    /// <summary>Names of the given workspaces, deleted ones included — the log outlives them.</summary>
    Task<Dictionary<Guid, AdminAuditWorkspaceName>> GetWorkspaceNamesAsync(
        IReadOnlyCollection<Guid> workspaceIds,
        CancellationToken ct = default);
}

/// <param name="Take">Rows to return; the caller asks for one more than it shows to learn whether
/// another page exists.</param>
/// <param name="AfterPerformedAt">Cursor: the last row already shown. Both halves or neither.</param>
/// <param name="EntityKey">Matched against entity_key; used when the subject filter is not a GUID.</param>
/// <param name="Text">Free text, already trimmed. Matched case-insensitively as a substring.</param>
public sealed record AdminAuditLogSearch(
    int Take,
    DateTime? AfterPerformedAt,
    Guid? AfterId,
    Guid? ActorId,
    string? Action,
    string? EntityType,
    Guid? EntityId,
    string? EntityKey,
    Guid? WorkspaceId,
    string? SourceService,
    string? Result,
    DateTime? From,
    DateTime? To,
    string? Text);

public sealed record AdminAuditFacetCount(string Value, int Count);

/// <param name="Email">The newest e-mail snapshot any of this actor's entries recorded.</param>
public sealed record AdminAuditActorCount(Guid ActorId, int Count, string? Email, string? Name);

public sealed record AdminAuditLogFacetRows(
    IReadOnlyList<AdminAuditFacetCount> Actions,
    IReadOnlyList<AdminAuditFacetCount> EntityTypes,
    IReadOnlyList<AdminAuditFacetCount> SourceServices,
    IReadOnlyList<AdminAuditActorCount> Actors);

public sealed record AdminAuditWorkspaceName(Guid Id, string Name, string? Slug);

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
    DateTime? To,
    // Allowlist on top of EntityType: a row must match one of these when the set is given.
    // The workspace-scoped read uses it so a category nobody reviewed for tenants stays hidden.
    IReadOnlyCollection<string>? EntityTypes = null);
