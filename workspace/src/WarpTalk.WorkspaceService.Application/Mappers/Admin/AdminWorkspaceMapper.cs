using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.ReadModels;

namespace WarpTalk.WorkspaceService.Application.Mappers.Admin;

public static class AdminWorkspaceMapper
{
    /// <summary>
    /// Soft delete wins over suspension: a deleted workspace is reported as deleted even
    /// though its is_active flag may still be true.
    /// </summary>
    public static string ToStatus(WorkspaceDirectoryRow row) =>
        row.DeletedAt != null
            ? WorkspaceLifecycleStatus.Deleted
            : row.IsActive
                ? WorkspaceLifecycleStatus.Active
                : WorkspaceLifecycleStatus.Suspended;

    /// <summary>
    /// Newest signal the workspace schema owns. Meeting-level activity is not visible from
    /// this service, so this is explicitly "last activity on the workspace record" — the
    /// meeting-based figure arrives with the per-workspace analytics API (WT-206).
    /// </summary>
    public static DateTime ToLastActivityAt(WorkspaceDirectoryRow row)
    {
        var lastActivity = row.UpdatedAt;
        if (row.LastMemberJoinedAt is { } memberJoinedAt && memberJoinedAt > lastActivity)
            lastActivity = memberJoinedAt;
        if (row.LastDocumentUploadedAt is { } documentUploadedAt && documentUploadedAt > lastActivity)
            lastActivity = documentUploadedAt;
        return lastActivity;
    }

    public static AdminWorkspaceOwnerDto ToOwner(Guid ownerId, User? owner) =>
        owner is null
            ? new AdminWorkspaceOwnerDto(ownerId, null, null, null, Resolved: false)
            : new AdminWorkspaceOwnerDto(ownerId, owner.FullName, owner.Email, owner.AvatarUrl, Resolved: true);

    public static AdminWorkspaceSummaryDto ToSummary(WorkspaceDirectoryRow row, User? owner) =>
        new(
            row.Id,
            row.Name,
            row.Slug,
            row.LogoUrl,
            ToStatus(row),
            ToOwner(row.OwnerId, owner),
            row.MemberCount,
            row.CreatedAt,
            row.UpdatedAt,
            ToLastActivityAt(row));

    public static AdminWorkspaceDetailDto ToDetail(
        WorkspaceDirectoryRow row,
        User? owner,
        IReadOnlyList<WorkspaceAdminAction> lifecycleHistory)
    {
        var history = lifecycleHistory.Select(ToLifecycleEvent).ToList();
        var latest = history.FirstOrDefault();

        return new AdminWorkspaceDetailDto(
            row.Id,
            row.Name,
            row.Slug,
            row.LogoUrl,
            ToStatus(row),
            ToOwner(row.OwnerId, owner),
            row.MemberCount,
            row.InternalMemberCount,
            row.ExternalMemberCount,
            row.PendingInvitationCount,
            row.DocumentCount,
            row.VerifiedDomainCount,
            row.AllowExternalCollaboration,
            row.RequireVerifiedDomainForInternal,
            row.CreatedAt,
            row.UpdatedAt,
            ToLastActivityAt(row),
            row.DeletedAt,
            // Only surface a current suspension while the workspace is actually suspended;
            // after reactivation the suspend row stays in history but stops being current.
            latest?.Action == WorkspaceAdminActionTypes.Suspend && row.DeletedAt == null && !row.IsActive
                ? latest
                : null,
            history);
    }

    public static AdminWorkspaceLifecycleEventDto ToLifecycleEvent(WorkspaceAdminAction action) =>
        new(action.Id, action.Action, action.Reason, action.PerformedBy, action.PerformedAt);
}
