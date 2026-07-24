using System;
using WarpTalk.BillingService.Domain.Constants;
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
            var memberResult = await _workspaceClient.GetWorkspaceMemberDetailsAsync(workspaceId, userId, cancellationToken);
            if (!memberResult.IsSuccess)
                return Result.Failure(memberResult.Error ?? BillingMessageConstants.ApiErrorMessages.BillingWorkspaceAuthError, ErrorCodes.InternalServerError);

            var memberDetails = memberResult.Value;
            if (!memberDetails.IsMember || !memberDetails.IsActive)
            {
                return Result.Failure(BillingMessageConstants.ApiErrorMessages.BillingWorkspaceAccessDenied, ErrorCodes.Forbidden);
            }

            var rolesArray = allowedRoles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var hasRole = rolesArray.Any(role => string.Equals(memberDetails.RoleName, role, StringComparison.OrdinalIgnoreCase));

            if (!hasRole)
            {
                return Result.Failure(string.Format(BillingMessageConstants.ApiErrorMessages.BillingWorkspaceRoleDenied, allowedRoles), ErrorCodes.Forbidden);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToAuthorizeUser, userId, workspaceId);
            return Result.Failure(BillingMessageConstants.ApiErrorMessages.BillingWorkspaceAuthError, ErrorCodes.InternalServerError);
        }
    }
}
