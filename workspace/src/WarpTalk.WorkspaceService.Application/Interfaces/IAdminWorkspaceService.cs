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
}
