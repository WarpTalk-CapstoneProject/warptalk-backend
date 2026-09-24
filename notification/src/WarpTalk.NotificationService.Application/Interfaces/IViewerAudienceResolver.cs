using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Interfaces;

/// <summary>
/// Who someone is, for deciding which targeted announcements they see: the workspaces they are an
/// active member of, those workspaces' plans, the roles they hold in them, and — only when an
/// announcement targets new accounts — when their account was created.
/// </summary>
public sealed record ViewerAudience(
    IReadOnlyCollection<Guid> WorkspaceIds,
    IReadOnlyCollection<string> PlanSlugs,
    IReadOnlyCollection<string> Roles,
    DateTime? AccountCreatedAt = null);

public interface IViewerAudienceResolver
{
    Task<Result<ViewerAudience>> ResolveAsync(Guid userId, bool includeAccountAge = false, CancellationToken ct = default);
}
