using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Admin;

/// <summary>
/// Query string contract for <c>GET /api/v1/admin/audit-log</c> and its CSV export. Bound with
/// [FromQuery]. Every filter is applied in the database; nothing is filtered after paging.
/// </summary>
public record AdminAuditLogQuery
{
    /// <summary>Opaque; the <c>nextCursor</c> of the previous page. Absent for the first page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Rows per page, 1–200. Defaults to 50.</summary>
    public int? Limit { get; init; }

    /// <summary>Inclusive lower bound on when the action was performed (UTC).</summary>
    public DateTime? From { get; init; }

    /// <summary>Exclusive upper bound (UTC).</summary>
    public DateTime? To { get; init; }

    /// <summary>Filter to one admin's actions.</summary>
    public Guid? ActorId { get; init; }

    /// <summary>Exact action verb, e.g. <c>suspend</c> or <c>plan.updated</c>.</summary>
    public string? Action { get; init; }

    /// <summary>See WarpTalk.Shared.Events.AdminAuditEntityTypes.</summary>
    public string? EntityType { get; init; }

    /// <summary>
    /// The subject: a GUID matches the entity id, anything else the natural key (a language code,
    /// a plugin key).
    /// </summary>
    public string? EntityId { get; init; }

    public Guid? WorkspaceId { get; init; }

    public string? SourceService { get; init; }

    /// <summary>"succeeded" or "failed".</summary>
    public string? Result { get; init; }

    /// <summary>
    /// Free text over the reason, action, subject name and key, actor e-mail and name, error,
    /// request id and IP — and the names of the workspaces the entries are filed under. A GUID
    /// matches any id on the entry exactly.
    /// </summary>
    public string? Q { get; init; }
}

public record AdminAuditLogPageDto(
    IReadOnlyList<AdminAuditLogEntryDto> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>Who performed the action. Name and e-mail are null when neither the entry nor the
/// account directory can supply them — never a placeholder.</summary>
public record AdminAuditActorDto(Guid Id, string? Name, string? Email);

/// <summary>What the action was taken on.</summary>
/// <param name="Id">The subject's GUID, when it has one.</param>
/// <param name="Key">The subject's natural key when it has no GUID (a language code, a plugin key).</param>
/// <param name="Label">What the subject is called: recorded at the time, or resolved now for a
/// workspace or an account.</param>
public record AdminAuditEntityDto(
    string Type,
    Guid? Id,
    string? Key,
    string? Label,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string? WorkspaceSlug);

/// <summary>The admin's own request, when the producing service recorded it.</summary>
/// <param name="CorrelationId">The request id: X-Correlation-ID or the service's trace id.</param>
public record AdminAuditRequestDto(string? CorrelationId, string? IpAddress, string? UserAgent);

/// <param name="BeforeSummary">
/// Already redacted at write time; redacted again on read so a row written before a redaction
/// rule existed still cannot leak.
/// </param>
public record AdminAuditLogEntryDto(
    Guid Id,
    DateTime PerformedAt,
    string SourceService,
    string Action,
    AdminAuditActorDto Actor,
    AdminAuditEntityDto Entity,
    string? Reason,
    string Result,
    string? ErrorMessage,
    AdminAuditRequestDto Request,
    IReadOnlyDictionary<string, string?>? BeforeSummary,
    IReadOnlyDictionary<string, string?>? AfterSummary);

public record AdminAuditFacetValueDto(string Value, int Count);

public record AdminAuditActorFacetDto(Guid Id, string? Name, string? Email, int Count);

/// <summary>The values the filter bar offers: every one present in the store, with how often.</summary>
public record AdminAuditLogFacetsDto(
    IReadOnlyList<AdminAuditFacetValueDto> Actions,
    IReadOnlyList<AdminAuditFacetValueDto> EntityTypes,
    IReadOnlyList<AdminAuditFacetValueDto> SourceServices,
    IReadOnlyList<AdminAuditActorFacetDto> Actors);

/// <summary>A rendered CSV export.</summary>
public record AdminAuditLogExport(byte[] Content, string FileName, int RowCount, bool Truncated);
