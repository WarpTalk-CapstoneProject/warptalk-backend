using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.Infrastructure.Clients;

public class WorkspaceClient : IWorkspaceClient
{
    private readonly WorkspaceService.WorkspaceServiceClient _grpcClient;
    private readonly ILogger<WorkspaceClient> _logger;

    public WorkspaceClient(
        WorkspaceService.WorkspaceServiceClient grpcClient,
        ILogger<WorkspaceClient> logger)
    {
        _grpcClient = grpcClient;
        _logger = logger;
    }

    public async Task<Result<(bool IsMember, string RoleName, bool IsActive)>> GetWorkspaceMemberDetailsAsync(
        Guid workspaceId, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new GetWorkspaceMemberRequest
            {
                WorkspaceId = workspaceId.ToString(),
                UserId = userId.ToString()
            };

            var response = await _grpcClient.GetWorkspaceMemberDetailsAsync(request, cancellationToken: cancellationToken);
            return Result.Success((response.IsMember, response.RoleName, response.IsActive));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToRetrieveWorkspaceMemberDetails, workspaceId, userId);
            return Result.Failure<(bool, string, bool)>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<Dictionary<Guid, string>>> GetWorkspaceNamesAsync(
        IEnumerable<Guid> workspaceIds, CancellationToken cancellationToken = default)
    {
        var distinctIds = workspaceIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
            return Result.Success(new Dictionary<Guid, string>());

        try
        {
            var request = new GetWorkspaceNamesRequest();
            request.WorkspaceIds.AddRange(distinctIds.Select(id => id.ToString()));

            var response = await _grpcClient.GetWorkspaceNamesAsync(request, cancellationToken: cancellationToken);

            var names = new Dictionary<Guid, string>();
            foreach (var item in response.Workspaces)
            {
                if (Guid.TryParse(item.WorkspaceId, out var workspaceId))
                    names[workspaceId] = item.WorkspaceName;
            }

            return Result.Success(names);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.FailedToResolveWorkspaceNames);
            return Result.Failure<Dictionary<Guid, string>>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<bool>> VerifyWorkspaceRolesAsync(
        Guid workspaceId, Guid userId, params string[] allowedRoles)
    {
        var memberResult = await GetWorkspaceMemberDetailsAsync(workspaceId, userId);
        if (!memberResult.IsSuccess)
            return Result.Failure<bool>(memberResult.Error ?? BillingMessageConstants.ApiErrorMessages.BillingWorkspaceAuthError, ErrorCodes.InternalServerError);

        var memberDetails = memberResult.Value;
        if (!memberDetails.IsMember || !memberDetails.IsActive)
            return Result.Success(false);

        foreach (var role in allowedRoles)
        {
            if (string.Equals(memberDetails.RoleName, role, StringComparison.OrdinalIgnoreCase))
                return Result.Success(true);
        }

        return Result.Success(false);
    }
}
