using Grpc.Core;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.NotificationService.API.Audience;

/// <summary>
/// WT-699 / TC4104: resolves BROADCAST through AuthService and SEGMENT (a workspace) through
/// WorkspaceService. Fails CLOSED on any lookup error — an announcement that silently reached a
/// partial audience would be reported Sent and never retried.
/// </summary>
public sealed class GrpcAdminAudienceResolver : IAdminAudienceResolver
{
    /// <summary>Largest audience one announcement may resolve to; past this, narrow the audience.</summary>
    public const int MaxRecipients = 100_000;
    private const int PageSize = 1000;

    private readonly UserService.UserServiceClient _users;
    private readonly WorkspaceService.WorkspaceServiceClient _workspaces;
    private readonly ILogger<GrpcAdminAudienceResolver> _logger;

    public GrpcAdminAudienceResolver(
        UserService.UserServiceClient users,
        WorkspaceService.WorkspaceServiceClient workspaces,
        ILogger<GrpcAdminAudienceResolver> logger)
    {
        _users = users;
        _workspaces = workspaces;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<Guid>>> ResolveAsync(
        string targetAudienceMode,
        Guid? segmentId,
        CancellationToken ct = default)
    {
        try
        {
            if (targetAudienceMode == NotificationConstants.TargetModeBroadcast)
                return await ResolveEveryoneAsync(ct);

            if (targetAudienceMode == NotificationConstants.TargetModeSegment && segmentId is { } workspaceId)
                return await ResolveWorkspaceAsync(workspaceId, ct);

            return Result.Failure<IReadOnlyList<Guid>>(
                $"Audience mode '{targetAudienceMode}' has no resolver.", ErrorCodes.ValidationError);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Could not resolve the {Mode} audience of an admin announcement.", targetAudienceMode);
            return Result.Failure<IReadOnlyList<Guid>>(
                "Could not resolve the audience right now. Nothing was sent; try again in a moment.",
                ErrorCodes.ServiceUnavailable);
        }
    }

    private async Task<Result<IReadOnlyList<Guid>>> ResolveEveryoneAsync(CancellationToken ct)
    {
        var ids = new List<Guid>();
        var token = string.Empty;
        do
        {
            var page = await _users.ListActiveUserIdsAsync(
                new ListActiveUserIdsRequest { PageToken = token, PageSize = PageSize },
                cancellationToken: ct);
            foreach (var raw in page.UserIds)
            {
                if (Guid.TryParse(raw, out var id)) ids.Add(id);
            }

            if (ids.Count > MaxRecipients)
                return TooMany();

            token = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(token));

        return Result.Success<IReadOnlyList<Guid>>(ids);
    }

    private async Task<Result<IReadOnlyList<Guid>>> ResolveWorkspaceAsync(Guid workspaceId, CancellationToken ct)
    {
        var members = await _workspaces.ListWorkspaceMemberUserIdsAsync(
            new ListWorkspaceMemberUserIdsRequest { WorkspaceId = workspaceId.ToString() },
            cancellationToken: ct);

        if (!members.WorkspaceFound)
            return Result.Failure<IReadOnlyList<Guid>>("The selected workspace does not exist.", ErrorCodes.ValidationError);

        var ids = members.UserIds.Select(raw => Guid.TryParse(raw, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
        return ids.Count > MaxRecipients ? TooMany() : Result.Success<IReadOnlyList<Guid>>(ids);
    }

    private static Result<IReadOnlyList<Guid>> TooMany() =>
        Result.Failure<IReadOnlyList<Guid>>(
            $"This audience is larger than {MaxRecipients:N0} people. Narrow it to a workspace.",
            ErrorCodes.ValidationError);
}

/// <summary>
/// Registered when AuthService/WorkspaceService addresses are not configured: BROADCAST and
/// SEGMENT are refused with a sentence that says why, instead of the service failing to start.
/// </summary>
public sealed class UnconfiguredAdminAudienceResolver : IAdminAudienceResolver
{
    public Task<Result<IReadOnlyList<Guid>>> ResolveAsync(string targetAudienceMode, Guid? segmentId, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<Guid>>(
            "Sending to everyone or to a workspace is not configured on this deployment (GrpcUrls:AuthServiceUrl / GrpcUrls:WorkspaceServiceUrl).",
            ErrorCodes.ServiceUnavailable));
}
