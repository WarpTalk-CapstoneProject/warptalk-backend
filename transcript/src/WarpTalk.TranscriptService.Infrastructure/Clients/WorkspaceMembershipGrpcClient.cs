using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.TranscriptService.Infrastructure.Clients;

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
            // Fail closed: an outage must not hand a caller access to a workspace's glossaries
            // just because workspace-service could not be asked whether they belong there.
            _logger.LogWarning(
                ex,
                "Workspace membership lookup failed for workspace {WorkspaceId} and user {UserId}.",
                workspaceId,
                userId);
            return WorkspaceMembership.None;
        }
    }
}
