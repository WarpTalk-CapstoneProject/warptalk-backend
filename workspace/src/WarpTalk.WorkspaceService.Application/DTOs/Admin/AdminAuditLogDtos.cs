using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.WorkspaceService.Application.DTOs.Admin;

/// <summary>
/// Query string contract for the admin audit log (WT-210). Bound with [FromQuery].
/// </summary>
public record AdminAuditLogQuery : AdminPageRequest
{
    /// <summary>Filter to one admin's actions.</summary>
    public Guid? ActorId { get; init; }

    /// <summary>Exact action name, e.g. "suspend".</summary>
    public string? Action { get; init; }

    /// <summary>See WarpTalk.Shared.Events.AdminAuditEntityTypes.</summary>
    public string? EntityType { get; init; }

    public Guid? EntityId { get; init; }

    public Guid? WorkspaceId { get; init; }

    public string? SourceService { get; init; }

    /// <summary>"succeeded" or "failed".</summary>
    public string? Result { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}

/// <param name="BeforeSummary">
/// Already redacted at write time; redacted again on read so a row written before a redaction
/// rule existed still cannot leak.
/// </param>
public record AdminAuditLogEntryDto(
    Guid Id,
    string SourceService,
    string Action,
    string EntityType,
    Guid? EntityId,
    Guid? WorkspaceId,
    Guid ActorId,
    string Reason,
    string Result,
    DateTime PerformedAt,
    string? CorrelationId,
    IReadOnlyDictionary<string, string?>? BeforeSummary,
    IReadOnlyDictionary<string, string?>? AfterSummary);
