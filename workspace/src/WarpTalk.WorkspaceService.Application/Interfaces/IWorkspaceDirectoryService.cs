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

    Task<Result<WorkspacePreflightDto>> GetPreflightAsync(
        Guid workspaceId,
        string? userEmail,
        CancellationToken ct = default);
}
