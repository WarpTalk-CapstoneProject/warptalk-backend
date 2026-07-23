using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IWorkspaceClient
{
    Task<(bool IsMember, string RoleName, bool IsActive)> GetWorkspaceMemberDetailsAsync(
        Guid workspaceId, Guid userId, CancellationToken cancellationToken = default);

    Task<bool> VerifyWorkspaceRolesAsync(
        Guid workspaceId, Guid userId, params string[] allowedRoles);
}
