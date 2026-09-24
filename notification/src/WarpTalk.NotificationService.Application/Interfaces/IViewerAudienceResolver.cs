using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Interfaces;

/// <summary>The workspaces a person is an active member of, and those workspaces' plans.</summary>
public sealed record ViewerAudience(IReadOnlyCollection<Guid> WorkspaceIds, IReadOnlyCollection<string> PlanSlugs);

/// <summary>
/// Who someone is, for deciding which targeted announcements they see. Answered by the workspace
/// service, which owns memberships and the replicated plan of each workspace.
/// </summary>
public interface IViewerAudienceResolver
{
    Task<Result<ViewerAudience>> ResolveAsync(Guid userId, CancellationToken ct = default);
}
