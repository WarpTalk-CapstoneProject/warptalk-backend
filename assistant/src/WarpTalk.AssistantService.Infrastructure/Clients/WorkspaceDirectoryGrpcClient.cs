using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AssistantService.Infrastructure.Clients;

/// <summary>
/// Name, slug and Owner of a workspace, from the workspace service's preflight RPC - the one that
/// already answers "what is this workspace" cheaply, without a verified-domain lookup when no email
/// is sent.
/// </summary>
public class WorkspaceDirectoryGrpcClient : IWorkspaceDirectoryClient
{
    private readonly WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly ILogger<WorkspaceDirectoryGrpcClient> _logger;

    public WorkspaceDirectoryGrpcClient(
        WorkspaceService.WorkspaceServiceClient workspaceClient,
        ILogger<WorkspaceDirectoryGrpcClient> logger)
    {
        _workspaceClient = workspaceClient;
        _logger = logger;
    }

    public async Task<WorkspaceProfile?> GetProfileAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var response = await _workspaceClient.GetWorkspacePreflightDetailsAsync(
                new GetWorkspacePreflightRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);

            return new WorkspaceProfile(
                workspaceId,
                string.IsNullOrWhiteSpace(response.WorkspaceName) ? "your workspace" : response.WorkspaceName,
                response.WorkspaceSlug,
                // Empty from a workspace service older than owner_user_id: no Owner to notify, which
                // the caller logs, rather than a parse failure.
                Guid.TryParse(response.OwnerUserId, out var ownerUserId) ? ownerUserId : null);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Could not resolve workspace {WorkspaceId} from the workspace service.", workspaceId);
            return null;
        }
    }

    public async Task<IReadOnlyList<Guid>?> ListActiveMemberUserIdsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var response = await _workspaceClient.ListWorkspaceMemberUserIdsAsync(
                new ListWorkspaceMemberUserIdsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);

            if (!response.WorkspaceFound) return null;

            return response.UserIds
                .Select(id => Guid.TryParse(id, out var userId) ? userId : Guid.Empty)
                .Where(userId => userId != Guid.Empty)
                .Distinct()
                .ToList();
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Could not list workspace {WorkspaceId}'s members from the workspace service.", workspaceId);
            return null;
        }
    }

    public async Task<IReadOnlyList<PlatformWorkspace>?> ListPlatformWorkspacesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _workspaceClient.ListPlatformWorkspacesAsync(
                new ListPlatformWorkspacesRequest(),
                cancellationToken: ct);

            return response.Workspaces
                .Where(item => Guid.TryParse(item.WorkspaceId, out _))
                .Select(item => new PlatformWorkspace(
                    Guid.Parse(item.WorkspaceId),
                    item.Name,
                    item.Slug,
                    item.Status,
                    Guid.TryParse(item.OwnerUserId, out var owner) ? owner : null,
                    string.IsNullOrWhiteSpace(item.PlanSlug) ? null : item.PlanSlug.Trim(),
                    item.MemberCount,
                    item.AllowAnyPlugins))
                .ToList();
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Could not list workspaces from the workspace service.");
            return null;
        }
    }

    public async Task<IReadOnlyList<(Guid UserId, Guid WorkspaceId)>?> ListActiveMembershipsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];

        try
        {
            var request = new ListActiveWorkspaceMembershipsRequest();
            request.UserIds.AddRange(userIds.Distinct().Select(id => id.ToString()));
            var response = await _workspaceClient.ListActiveWorkspaceMembershipsAsync(request, cancellationToken: ct);

            return response.Memberships
                .Select(pair => (
                    UserId: Guid.TryParse(pair.UserId, out var user) ? user : Guid.Empty,
                    WorkspaceId: Guid.TryParse(pair.WorkspaceId, out var workspace) ? workspace : Guid.Empty))
                .Where(pair => pair.UserId != Guid.Empty && pair.WorkspaceId != Guid.Empty)
                .ToList();
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Could not list workspace memberships for {Count} user(s).", userIds.Count);
            return null;
        }
    }
}
