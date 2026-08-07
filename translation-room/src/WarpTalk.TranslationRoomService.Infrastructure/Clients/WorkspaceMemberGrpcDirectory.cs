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
        var member = await GetMemberAsync(workspaceId, userId, ct);

        if (member is null || !member.IsMember || !member.IsActive)
        {
            return false;
        }

        return string.Equals(member.RoleName, OwnerRole, StringComparison.OrdinalIgnoreCase)
            || string.Equals(member.RoleName, AdminRole, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsActiveMemberAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct = default)
    {
        var member = await GetMemberAsync(workspaceId, userId, ct);

        return member is not null && member.IsMember && member.IsActive;
    }

    /// <summary>
    /// Null when WorkspaceService could not answer. Deliberately swallowed: both callers only ever
    /// *widen* a decision the caller has already denied, so letting a WorkspaceService outage bubble
    /// up would turn "you are not the host" into a 500 for legitimate users.
    /// </summary>
    private async Task<GetWorkspaceMemberResponse?> GetMemberAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken ct)
    {
        try
        {
            return await _client.GetWorkspaceMemberDetailsAsync(
                new GetWorkspaceMemberRequest
                {
                    WorkspaceId = workspaceId.ToString(),
                    UserId = userId.ToString()
                },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve workspace membership. WorkspaceId: {WorkspaceId}, UserId: {UserId}",
                workspaceId,
                userId);
            return null;
        }
    }
}
