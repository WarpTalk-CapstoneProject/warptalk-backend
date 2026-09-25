using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.WorkspaceService.Application.DTOs.Admin;

// G12 pending-work inbox, as the web reads it (src/types/admin-inbox.ts). camelCase on the wire.

/// <summary>How one source answered. <paramref name="Status"/>: ok | unavailable | forbidden.</summary>
public sealed record AdminInboxSourceStatusDto(
    string Source,
    string Status,
    int ItemCount,
    bool Truncated,
    long DurationMs,
    string? Error);

public sealed record AdminInboxTriageDto(
    Guid? AssigneeId,
    string? AssigneeName,
    DateTime? AssignedAt,
    DateTime? SnoozedUntil,
    DateTime? DoneAt,
    Guid? DoneBy,
    int NoteCount,
    DateTime? LastNoteAt);

/// <summary>One item: what its source says, plus the triage staff added.</summary>
public sealed record AdminInboxItemDto(
    string Key,
    string Source,
    string Type,
    string Title,
    string? Detail,
    Guid? WorkspaceId,
    string? Customer,
    DateTime OccurredAt,
    DateTime? DueAt,
    string Priority,
    string Href,
    bool NaturalCompletion,
    decimal? Amount,
    string? Currency,
    bool Overdue,
    bool Snoozed,
    bool Done,
    AdminInboxTriageDto Triage);

public sealed record AdminInboxCountsDto(int Open, int Mine, int Unassigned, int Overdue, int Snoozed, int Done);

/// <summary><c>GET ~/api/v1/admin/inbox</c>.</summary>
public sealed record AdminInboxDto(
    DateTime GeneratedAt,
    Guid ViewerId,
    IReadOnlyList<AdminInboxItemDto> Items,
    IReadOnlyList<AdminInboxSourceStatusDto> Sources,
    AdminInboxCountsDto Counts);

/// <summary><c>GET ~/api/v1/admin/inbox/summary</c>: the sidebar badge.</summary>
public sealed record AdminInboxSummaryDto(DateTime GeneratedAt, AdminInboxCountsDto Counts, int UnavailableSources);

public sealed record AdminInboxNoteDto(Guid Id, string ItemKey, string Body, Guid AuthorId, string? AuthorName, DateTime CreatedAt);

public sealed record AdminInboxAssignRequest(string Key, Guid? AssigneeId);

public sealed record AdminInboxSnoozeRequest(string Key, DateTime? Until);

public sealed record AdminInboxKeyRequest(string Key);

public sealed record AdminInboxNoteRequest(string Key, string Body);

/// <summary>What a source client returns: the response, or why there is none.</summary>
public sealed record AdminInboxSourceResult(
    string Source,
    AdminInboxSourceResponse? Response,
    string Status,
    long DurationMs,
    string? Error);
