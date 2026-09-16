using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Application.DTOs.Entitlements;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceEntitlementService
{
    /// <summary>
    /// The resolved entitlement snapshot for a workspace, with provenance. Any active member may
    /// read it — the same audience as GET settings, which already hands members the room and
    /// language ceilings derived from this snapshot.
    /// </summary>
    Task<Result<WorkspaceEntitlementsDto>> GetEntitlementsAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default);
}
