using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AssistantService.Infrastructure.Clients;

public class WorkspaceMembershipGrpcClient : IWorkspaceMembershipClient
{
    private readonly WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly ILogger<WorkspaceMembershipGrpcClient> _logger;

    public WorkspaceMembershipGrpcClient(
        WorkspaceService.WorkspaceServiceClient workspaceClient,
        ILogger<WorkspaceMembershipGrpcClient> logger)
    {
        _workspaceClient = workspaceClient;
        _logger = logger;
    }

    public async Task<WorkspaceMembership> GetMembershipAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _workspaceClient.GetWorkspaceMemberDetailsAsync(
                new GetWorkspaceMemberRequest
                {
                    WorkspaceId = workspaceId.ToString(),
                    UserId = userId.ToString(),
                },
                cancellationToken: ct);

            return new WorkspaceMembership(response.IsMember, response.RoleName, response.IsActive);
        }
        catch (RpcException ex)
        {
            // Fail closed, unlike the gateway's membership lookup: there, membership could only
            // widen a decision already denied on host identity, so an outage had to degrade to
            // "not an admin" without denying a legitimate caller. Here it is the only thing
            // separating a Member from an Owner on a workspace that has confined installation to
            // Owner and Admin, so an outage must not hand out the Owner's answer.
            _logger.LogWarning(
                ex,
                "Workspace membership lookup failed for workspace {WorkspaceId} and user {UserId}.",
                workspaceId,
                userId);
            return WorkspaceMembership.None;
        }
    }
}
