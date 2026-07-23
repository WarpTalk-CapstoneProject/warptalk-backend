using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
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

    public async Task<(bool IsMember, string RoleName, bool IsActive)> GetWorkspaceMemberDetailsAsync(
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
            return (response.IsMember, response.RoleName, response.IsActive);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve workspace member details via gRPC for Workspace: {WorkspaceId}, User: {UserId}", workspaceId, userId);
            return (false, string.Empty, false);
        }
    }

    public async Task<bool> VerifyWorkspaceRolesAsync(
        Guid workspaceId, Guid userId, params string[] allowedRoles)
    {
        var memberDetails = await GetWorkspaceMemberDetailsAsync(workspaceId, userId);

        if (!memberDetails.IsMember || !memberDetails.IsActive)
            return false;

        foreach (var role in allowedRoles)
        {
            if (string.Equals(memberDetails.RoleName, role, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
