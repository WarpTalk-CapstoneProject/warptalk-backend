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

    Task<Result<IReadOnlyList<WorkspaceNameDto>>> GetWorkspaceNamesAsync(
        IReadOnlyCollection<Guid> workspaceIds,
        CancellationToken ct = default);

    Task<Result<MeetingCreationDecisionDto>> ValidateMeetingCreationAsync(
        Guid workspaceId,
        Guid userId,
        IReadOnlyCollection<string> targetLanguages,
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
