using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IWorkspaceClient
{
    Task<Result<(bool IsMember, string RoleName, bool IsActive)>> GetWorkspaceMemberDetailsAsync(
        Guid workspaceId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves workspace display names for admin/global billing views. The workspace schema is
    /// owned by workspace-service, so billing must never query its tables directly — this goes
    /// over gRPC (boundary enforced by warptalk-infrastructure/scripts/check-production-deployment.sh).
    /// </summary>
    Task<Result<Dictionary<Guid, string>>> GetWorkspaceNamesAsync(
        IEnumerable<Guid> workspaceIds, CancellationToken cancellationToken = default);

    Task<Result<bool>> VerifyWorkspaceRolesAsync(
        Guid workspaceId, Guid userId, params string[] allowedRoles);
}
