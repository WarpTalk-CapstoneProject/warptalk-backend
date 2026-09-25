using System.Globalization;
using Grpc.Core;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.NotificationService.API.Audience;

/// <summary>
/// Audience sends ask auth for accounts and the workspace service for memberships and plans.
/// Fails closed: a lookup error refuses the send rather than reaching part of its audience.
/// </summary>
public sealed class GrpcEmailRecipientDirectory : IEmailRecipientDirectory
{
    private const int PageSize = 1000;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly UserService.UserServiceClient _users;
    private readonly WorkspaceService.WorkspaceServiceClient _workspaces;
    private readonly ILogger<GrpcEmailRecipientDirectory> _logger;

    public GrpcEmailRecipientDirectory(
        UserService.UserServiceClient users,
        WorkspaceService.WorkspaceServiceClient workspaces,
        ILogger<GrpcEmailRecipientDirectory> logger)
    {
        _users = users;
        _workspaces = workspaces;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<Guid>>> ListActiveUserIdsAsync(int limit, CancellationToken ct = default)
    {
        try
        {
            var ids = new List<Guid>();
            var token = string.Empty;
            do
            {
                var page = await _users.ListActiveUserIdsAsync(
                    new ListActiveUserIdsRequest { PageToken = token, PageSize = PageSize },
                    deadline: DateTime.UtcNow.Add(CallTimeout),
                    cancellationToken: ct);
                ids.AddRange(page.UserIds.Select(raw => Guid.TryParse(raw, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty));
                token = page.NextPageToken;
            }
            while (!string.IsNullOrEmpty(token) && ids.Count < limit);
            return Result.Success<IReadOnlyList<Guid>>(ids);
        }
        catch (RpcException ex)
        {
            return Unavailable(ex, "accounts");
        }
    }

    public async Task<Result<IReadOnlyList<Guid>>> ListMembersOfPlansAsync(IReadOnlyCollection<string> planSlugs, int limit, CancellationToken ct = default)
    {
        try
        {
            var workspaces = await _workspaces.ListPlatformWorkspacesAsync(
                new ListPlatformWorkspacesRequest(), deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct);
            var matching = workspaces.Workspaces
                .Where(w => w.Status == "active" && planSlugs.Contains(w.PlanSlug, StringComparer.OrdinalIgnoreCase))
                .Select(w => Guid.TryParse(w.WorkspaceId, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList();
            return await ListMembersOfWorkspacesAsync(matching, limit, ct);
        }
        catch (RpcException ex)
        {
            return Unavailable(ex, "workspace plans");
        }
    }

    public async Task<Result<IReadOnlyList<Guid>>> ListMembersOfWorkspacesAsync(IReadOnlyCollection<Guid> workspaceIds, int limit, CancellationToken ct = default)
    {
        try
        {
            var ids = new HashSet<Guid>();
            foreach (var workspaceId in workspaceIds)
            {
                var members = await _workspaces.ListWorkspaceMemberUserIdsAsync(
                    new ListWorkspaceMemberUserIdsRequest { WorkspaceId = workspaceId.ToString() },
                    deadline: DateTime.UtcNow.Add(CallTimeout),
                    cancellationToken: ct);
                foreach (var raw in members.UserIds)
                {
                    if (Guid.TryParse(raw, out var id)) ids.Add(id);
                }
                if (ids.Count >= limit) break;
            }
            return Result.Success<IReadOnlyList<Guid>>(ids.ToList());
        }
        catch (RpcException ex)
        {
            return Unavailable(ex, "workspace members");
        }
    }

    public async Task<EmailRecipientCandidate?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetUserByIdAsync(
            new GetUserRequest { Id = userId.ToString() }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(user.Id) || string.IsNullOrWhiteSpace(user.Email)) return null;
        DateTime? created = DateTime.TryParse(user.CreatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        return new EmailRecipientCandidate(
            userId,
            user.Email,
            string.IsNullOrWhiteSpace(user.FullName) ? null : user.FullName,
            string.IsNullOrWhiteSpace(user.PreferredLanguage) ? null : user.PreferredLanguage,
            created);
    }

    private Result<IReadOnlyList<Guid>> Unavailable(RpcException ex, string what)
    {
        _logger.LogWarning(ex, "Could not list {What} for an audience send.", what);
        return Result.Failure<IReadOnlyList<Guid>>(
            $"Could not read {what} right now. Nothing was sent; try again in a moment.", ErrorCodes.ServiceUnavailable);
    }
}

/// <summary>No auth/workspace addresses configured: audience sends are refused with a reason.</summary>
public sealed class UnconfiguredEmailRecipientDirectory : IEmailRecipientDirectory
{
    private static Task<Result<IReadOnlyList<Guid>>> Refuse() =>
        Task.FromResult(Result.Failure<IReadOnlyList<Guid>>(
            "Audience sends need GrpcUrls:AuthServiceUrl and GrpcUrls:WorkspaceServiceUrl.", ErrorCodes.ServiceUnavailable));

    public Task<Result<IReadOnlyList<Guid>>> ListActiveUserIdsAsync(int limit, CancellationToken ct = default) => Refuse();

    public Task<Result<IReadOnlyList<Guid>>> ListMembersOfPlansAsync(IReadOnlyCollection<string> planSlugs, int limit, CancellationToken ct = default) => Refuse();

    public Task<Result<IReadOnlyList<Guid>>> ListMembersOfWorkspacesAsync(IReadOnlyCollection<Guid> workspaceIds, int limit, CancellationToken ct = default) => Refuse();

    public Task<EmailRecipientCandidate?> GetAsync(Guid userId, CancellationToken ct = default) => Task.FromResult<EmailRecipientCandidate?>(null);
}
