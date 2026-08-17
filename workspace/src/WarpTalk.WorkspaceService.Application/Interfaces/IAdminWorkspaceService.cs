using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// Platform-wide workspace administration (WT-204). Every method is global by construction:
/// none of them take a caller workspace, and none of them consult workspace membership.
/// Authorization is the controller's job — see AdminWorkspacesController.
/// </summary>
public interface IAdminWorkspaceService
{
    Task<Result<AdminPagedResult<AdminWorkspaceSummaryDto>>> GetDirectoryAsync(
        AdminWorkspaceDirectoryQuery query,
        CancellationToken ct = default);

    Task<Result<AdminWorkspaceDetailDto>> GetDetailAsync(Guid workspaceId, CancellationToken ct = default);

    Task<Result<AdminWorkspaceDetailDto>> SuspendAsync(
        Guid workspaceId,
        string reason,
        Guid actorId,
        string? correlationId,
        CancellationToken ct = default);

    Task<Result<AdminWorkspaceDetailDto>> ReactivateAsync(
        Guid workspaceId,
        string reason,
        Guid actorId,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>
    /// Soft delete with the same member semantics as the Owner's own delete (WT-417): the
    /// membership rows go with the workspace, so nothing is left holding a UNIQUE slot against a
    /// future rejoin. Records an audit row in the same transaction. Irreversible from this API.
    /// </summary>
    Task<Result<AdminWorkspaceDetailDto>> DeleteAsync(
        Guid workspaceId,
        string reason,
        Guid actorId,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>Active member roster with identities resolved from Auth. Read-only.</summary>
    Task<Result<System.Collections.Generic.IReadOnlyList<AdminWorkspaceMemberDto>>> GetMembersAsync(
        Guid workspaceId,
        CancellationToken ct = default);
}
