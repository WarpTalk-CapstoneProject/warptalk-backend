using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Clients;

public sealed class WorkspaceMemberGrpcDirectory : IWorkspaceMemberDirectory
{
    private const string OwnerRole = "Owner";
    private const string AdminRole = "Admin";

    private readonly WorkspaceService.WorkspaceServiceClient _client;
    private readonly ILogger<WorkspaceMemberGrpcDirectory> _logger;

    public WorkspaceMemberGrpcDirectory(
        WorkspaceService.WorkspaceServiceClient client,
        ILogger<WorkspaceMemberGrpcDirectory> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> IsOwnerOrAdminAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetWorkspaceMemberDetailsAsync(
                new GetWorkspaceMemberRequest
                {
                    WorkspaceId = workspaceId.ToString(),
                    UserId = userId.ToString()
                },
                cancellationToken: ct);

            if (!response.IsMember || !response.IsActive)
            {
                return false;
            }

            return string.Equals(response.RoleName, OwnerRole, StringComparison.OrdinalIgnoreCase)
                || string.Equals(response.RoleName, AdminRole, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed: this only ever *widens* an authorization decision the caller
            // has already denied on host identity. Letting a WorkspaceService outage bubble up would
            // turn "you are not the host" into a 500 for legitimate non-host users.
            _logger.LogWarning(
                ex,
                "Failed to resolve workspace membership. WorkspaceId: {WorkspaceId}, UserId: {UserId}",
                workspaceId,
                userId);
            return false;
        }
    }

    public async Task<bool> IsMemberAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetWorkspaceMemberDetailsAsync(
                new GetWorkspaceMemberRequest
                {
                    WorkspaceId = workspaceId.ToString(),
                    UserId = userId.ToString()
                },
                cancellationToken: ct);

            return response.IsMember && response.IsActive;
        }
        catch (Exception ex)
        {
            // WT-433: false on failure keeps the join-by-id gate FAIL-CLOSED — an unreachable
            // WorkspaceService means the link-arriving caller sees NotFound, the same answer a
            // non-member gets, rather than being admitted on an unverifiable claim.
            _logger.LogWarning(
                ex,
                "Failed to resolve workspace membership for join-by-id. WorkspaceId: {WorkspaceId}, UserId: {UserId}",
                workspaceId,
                userId);
            return false;
        }
    }
}
