using System;

namespace WarpTalk.WorkspaceService.Domain.ReadModels;

/// <summary>
/// Server-side filter for the system-admin workspace directory. Every field is applied in
/// SQL — the caller's active workspace never narrows the result set.
/// </summary>
public sealed record WorkspaceDirectoryFilter(
    int Page,
    int PageSize,
    string? Search,
    string Status,
    int? MinMembers,
    int? MaxMembers,
    string Sort);

/// <summary>
/// Projection behind both the directory list and the workspace detail. Counts are computed in
/// SQL for the requested page only, so a page of 20 never loads 20 workspaces' member rows.
/// </summary>
/// <param name="LastMemberJoinedAt">
/// Newest member join within this workspace. Combined with <paramref name="UpdatedAt"/> and
/// <paramref name="LastDocumentUploadedAt"/> this is the best "last activity" signal the
/// workspace schema owns — meeting-level activity lives in the meeting service (WT-206).
/// </param>
public sealed record WorkspaceDirectoryRow(
    Guid Id,
    string Name,
    string Slug,
    string? LogoUrl,
    Guid OwnerId,
    bool IsActive,
    DateTime? DeletedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool AllowExternalCollaboration,
    bool RequireVerifiedDomainForInternal,
    int MemberCount,
    int InternalMemberCount,
    int ExternalMemberCount,
    int PendingInvitationCount,
    int DocumentCount,
    int VerifiedDomainCount,
    DateTime? LastMemberJoinedAt,
    DateTime? LastDocumentUploadedAt);
