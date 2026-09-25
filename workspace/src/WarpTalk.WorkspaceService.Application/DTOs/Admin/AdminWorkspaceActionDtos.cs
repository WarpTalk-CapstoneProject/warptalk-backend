using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Admin;

// ── The admin workspace page (ERP-style detail), workspace-service half. ────────────────────────
//
// Every write takes a reason, is system-admin gated, and lands in the platform audit log. The
// actor is resolved from the token; no body names one.

/// <param name="NewOwnerUserId">Must be an active, internal member of the workspace.</param>
public sealed record AdminTransferOwnershipRequest(Guid NewOwnerUserId, string Reason);

/// <summary>A notice to the workspace owner, delivered through the notification service.</summary>
public sealed record AdminWorkspaceNoticeRequest(string Title, string Message, string Reason);

/// <summary>A note is its own reason: the body is what gets recorded.</summary>
public sealed record AdminAddWorkspaceNoteRequest(string Body);

public sealed record AdminWorkspaceExportRequest(string Reason);

public sealed record AdminWorkspaceNoteDto(
    Guid Id,
    string Body,
    Guid AuthorId,
    string? AuthorName,
    DateTime CreatedAt);

/// <summary>
/// One line of a workspace's timeline: an audit-log action from any service (lifecycle, credits,
/// plan, invoices, sign-outs…) or an internal note. <paramref name="Kind"/> is <c>action</c> or
/// <c>note</c>; <paramref name="Text"/> is the action's reason or the note's body.
/// </summary>
public sealed record AdminWorkspaceTimelineEntryDto(
    string Kind,
    Guid Id,
    DateTime At,
    string? Action,
    string? SourceService,
    string? EntityType,
    Guid? EntityId,
    string? Result,
    string Text,
    Guid ActorId,
    string? ActorName,
    IReadOnlyDictionary<string, string?>? Before,
    IReadOnlyDictionary<string, string?>? After);

public sealed record AdminWorkspaceNoticeResultDto(Guid OwnerId, string? NotificationId, DateTime SentAt);

/// <summary>
/// The workspace-owned half of the data summary: the record, its roster and its timeline. The page
/// adds the billing overview and meeting totals it already holds before it hands the file over.
/// Membership facts and operational history only — no tenant content.
/// </summary>
public sealed record AdminWorkspaceExportDto(
    DateTime GeneratedAt,
    Guid GeneratedBy,
    AdminWorkspaceDetailDto Workspace,
    IReadOnlyList<AdminWorkspaceMemberDto> Members,
    IReadOnlyList<AdminWorkspaceTimelineEntryDto> Timeline);
