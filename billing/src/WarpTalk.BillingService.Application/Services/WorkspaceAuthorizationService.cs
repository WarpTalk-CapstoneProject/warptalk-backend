using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class WorkspaceAuthorizationService : IWorkspaceAuthorizationService
{
    private readonly IWorkspaceClient _workspaceClient;
    private readonly ILogger<WorkspaceAuthorizationService> _logger;

    public WorkspaceAuthorizationService(
        IWorkspaceClient workspaceClient,
        ILogger<WorkspaceAuthorizationService> logger)
    {
        _workspaceClient = workspaceClient;
        _logger = logger;
    }

    public async Task<Result> AuthorizeAsync(Guid workspaceId, Guid userId, string allowedRoles, CancellationToken cancellationToken = default)
    {
        try
        {
            var memberDetails = await _workspaceClient.GetWorkspaceMemberDetailsAsync(workspaceId, userId, cancellationToken);

            if (!memberDetails.IsMember || !memberDetails.IsActive)
            {
                return Result.Failure("Access denied. You are not an active member of this workspace.", ErrorCodes.Forbidden);
            }

            var rolesArray = allowedRoles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var hasRole = rolesArray.Any(role => string.Equals(memberDetails.RoleName, role, StringComparison.OrdinalIgnoreCase));

            if (!hasRole)
            {
                return Result.Failure($"Access denied. You must be one of the following roles: {allowedRoles}", ErrorCodes.Forbidden);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authorize user {UserId} for workspace {WorkspaceId}", userId, workspaceId);
            return Result.Failure("An error occurred during workspace authorization.", ErrorCodes.InternalServerError);
        }
    }
}
