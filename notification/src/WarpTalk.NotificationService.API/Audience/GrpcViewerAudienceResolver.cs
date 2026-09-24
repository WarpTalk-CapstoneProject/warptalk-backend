using Grpc.Core;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.NotificationService.API.Audience;

/// <summary>
/// Asks the workspace service which workspaces someone belongs to and on which plans, so a
/// plan- or workspace-targeted announcement is shown to exactly its audience.
/// </summary>
public sealed class GrpcViewerAudienceResolver : IViewerAudienceResolver
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly WorkspaceService.WorkspaceServiceClient _workspaces;
    private readonly ILogger<GrpcViewerAudienceResolver> _logger;

    public GrpcViewerAudienceResolver(
        WorkspaceService.WorkspaceServiceClient workspaces,
        ILogger<GrpcViewerAudienceResolver> logger)
    {
        _workspaces = workspaces;
        _logger = logger;
    }

    public async Task<Result<ViewerAudience>> ResolveAsync(Guid userId, CancellationToken ct = default)
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
            return Result.Success(new ViewerAudience(workspaceIds, planSlugs));
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
    public Task<Result<ViewerAudience>> ResolveAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<ViewerAudience>(
            "Targeted announcements need GrpcUrls:WorkspaceServiceUrl.", ErrorCodes.ServiceUnavailable));
}
