using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Workspace lookups and policy decisions consumed by other services over gRPC.
/// The gRPC boundary owns request parsing and response mapping; membership rules,
/// workspace configuration and verified-domain matching all live here.
/// </summary>
public interface IWorkspaceDirectoryService
{
    /// <summary>
    /// Returns the member's details, or a success with a null value when the user is
    /// not a member — non-membership is a normal answer, not a failure.
    ///
    /// <see cref="WorkspaceMemberDetailsDto.IsActive"/> is the MEMBER's status and says nothing
    /// about the workspace's own lifecycle: a suspended workspace still reports its members as
    /// active. Anything that must stop when an admin suspends a tenant asks
    /// <see cref="GetPreflightAsync"/> instead.
    /// </summary>
    Task<Result<WorkspaceMemberDetailsDto?>> GetMemberDetailsAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// WT-699 / TC4104: every ACTIVE member of a workspace, by user id — the audience of a SEGMENT
    /// admin announcement. Null when the workspace does not exist (or was deleted), so an unknown
    /// segment is refused rather than read as "nobody".
    /// </summary>
    Task<Result<IReadOnlyList<Guid>?>> ListActiveMemberUserIdsAsync(
        Guid workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// The workspaces one person is an active member of, each with its replicated plan slug —
    /// who a plan- or workspace-targeted announcement is shown to.
    /// </summary>
    Task<Result<IReadOnlyList<UserWorkspaceAudienceDto>>> ListUserWorkspaceAudienceAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Every workspace that is not deleted, with its plan, Owner, active member count and the legacy
    /// AllowAnyPlugins switch - for the platform admin's per-workspace plugin controls.
    /// </summary>
    Task<Result<IReadOnlyList<PlatformWorkspaceDto>>> ListPlatformWorkspacesAsync(CancellationToken ct = default);

    /// <summary>The active, non-deleted workspaces each given user belongs to.</summary>
    Task<Result<IReadOnlyList<(Guid UserId, Guid WorkspaceId)>>> ListActiveMembershipsForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<WorkspaceNameDto>>> GetWorkspaceNamesAsync(
        IReadOnlyCollection<Guid> workspaceIds,
        CancellationToken ct = default);

    Task<Result<MeetingCreationDecisionDto>> ValidateMeetingCreationAsync(
        Guid workspaceId,
        Guid userId,
        IReadOnlyCollection<string> targetLanguages,
        // WT-466: the room's source language, checked against the same workspace whitelist as the
        // targets. Null or empty means "not stated" and is never a violation.
        string? sourceLanguage = null,
        CancellationToken ct = default);

    Task<Result<WorkspaceSettingsSnapshotDto>> GetSettingsAsync(
        Guid workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// The workspace's tenant-lifecycle answer, plus the naming and domain facts a join screen
    /// needs. <c>userEmail</c> is optional: pass null or empty and the verified-domain lookup is
    /// skipped entirely, which is how TranslationRoomService uses this as a cheap
    /// "is this tenant still live?" check on the room join and start paths.
    /// </summary>
    Task<Result<WorkspacePreflightDto>> GetPreflightAsync(
        Guid workspaceId,
        string? userEmail,
        CancellationToken ct = default);
}
