using System.Globalization;
using Grpc.Core;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.NotificationService.API.Audience;

/// <summary>
/// Asks the workspace service which workspaces someone belongs to, on which plans and in which
/// roles, and — only when an announcement targets new accounts — asks auth when the account was
/// created. So a targeted announcement is shown to exactly its audience.
/// </summary>
public sealed class GrpcViewerAudienceResolver : IViewerAudienceResolver
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly WorkspaceService.WorkspaceServiceClient _workspaces;
    private readonly UserService.UserServiceClient? _users;
    private readonly ILogger<GrpcViewerAudienceResolver> _logger;

    public GrpcViewerAudienceResolver(
        WorkspaceService.WorkspaceServiceClient workspaces,
        ILogger<GrpcViewerAudienceResolver> logger,
        UserService.UserServiceClient? users = null)
    {
        _workspaces = workspaces;
        _logger = logger;
        _users = users;
    }

    public async Task<Result<ViewerAudience>> ResolveAsync(Guid userId, bool includeAccountAge = false, CancellationToken ct = default)
    {
        try
        {
            var response = await _workspaces.ListUserWorkspaceAudienceAsync(
                new ListUserWorkspaceAudienceRequest { UserId = userId.ToString() },
                deadline: DateTime.UtcNow.Add(CallTimeout),
                cancellationToken: ct);

            var workspaceIds = response.Workspaces
                .Select(item => Guid.TryParse(item.WorkspaceId, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToHashSet();
            var planSlugs = response.Workspaces
                .Select(item => item.PlanSlug)
                .Where(slug => !string.IsNullOrWhiteSpace(slug))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var roles = response.Workspaces
                .Select(item => item.RoleName)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            DateTime? createdAt = null;
            if (includeAccountAge && _users is not null)
            {
                var user = await _users.GetUserByIdAsync(
                    new GetUserRequest { Id = userId.ToString() },
                    deadline: DateTime.UtcNow.Add(CallTimeout),
                    cancellationToken: ct);
                if (DateTime.TryParse(user.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                    createdAt = parsed;
            }

            return Result.Success(new ViewerAudience(workspaceIds, planSlugs, roles, createdAt));
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Could not resolve the workspaces of {UserId} for announcement targeting.", userId);
            return Result.Failure<ViewerAudience>("Could not resolve the viewer's workspaces.", ErrorCodes.ServiceUnavailable);
        }
    }
}

/// <summary>No workspace service configured: targeted announcements are shown to nobody.</summary>
public sealed class UnconfiguredViewerAudienceResolver : IViewerAudienceResolver
{
    public Task<Result<ViewerAudience>> ResolveAsync(Guid userId, bool includeAccountAge = false, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<ViewerAudience>(
            "Targeted announcements need GrpcUrls:WorkspaceServiceUrl.", ErrorCodes.ServiceUnavailable));
}
