using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.WorkspaceService.Application.DTOs.WorkspaceAuditLog;

/// <summary>
/// Query string for the workspace-scoped audit log. Bound with [FromQuery].
///
/// Deliberately narrower than <c>AdminAuditLogQuery</c>: there is no WorkspaceId (the route
/// supplies it, so a client cannot widen the scope), no ActorId (every actor is redacted, so
/// filtering on one would leak whether a given staff id acted), and no Result (failed staff
/// attempts are never shown to a tenant).
/// </summary>
public record WorkspaceAuditLogQuery : AdminPageRequest
{
    /// <summary>Exact action name, e.g. "suspend".</summary>
    public string? Action { get; init; }

    /// <summary>Must be one of the tenant-visible entity types; anything else returns nothing.</summary>
    public string? EntityType { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}

/// <summary>
/// One tenant-visible audit entry.
/// </summary>
/// <param name="ActorType">"staff" for every row today: the underlying store only records
/// platform-administrator actions.</param>
/// <param name="ActorDisplayName">Always "WarpTalk staff". The individual administrator's id
/// is never returned to a tenant.</param>
public record WorkspaceAuditLogEntryDto(
    Guid Id,
    string Action,
    string EntityType,
    Guid? EntityId,
    string ActorType,
    string ActorDisplayName,
    DateTime PerformedAt,
    IReadOnlyDictionary<string, string?>? BeforeSummary,
    IReadOnlyDictionary<string, string?>? AfterSummary);
