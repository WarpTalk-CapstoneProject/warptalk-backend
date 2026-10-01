using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Interfaces;

/// <summary>One person an audience send may reach, as the account directory knows them.</summary>
public sealed record EmailRecipientCandidate(Guid UserId, string Email, string? FullName, string? PreferredLanguage, DateTime? CreatedAt);

/// <summary>
/// Who exists, for audience sends: the notification service does not own accounts or workspaces,
/// so it asks the services that do (auth for accounts, workspace for memberships and plans).
/// </summary>
public interface IEmailRecipientDirectory
{
    /// <summary>Every active account.</summary>
    Task<Result<IReadOnlyList<Guid>>> ListActiveUserIdsAsync(int limit, CancellationToken ct = default);

    /// <summary>Active members of workspaces on any of these plans.</summary>
    Task<Result<IReadOnlyList<Guid>>> ListMembersOfPlansAsync(IReadOnlyCollection<string> planSlugs, int limit, CancellationToken ct = default);

    /// <summary>Active members of these workspaces.</summary>
    Task<Result<IReadOnlyList<Guid>>> ListMembersOfWorkspacesAsync(IReadOnlyCollection<Guid> workspaceIds, int limit, CancellationToken ct = default);

    /// <summary>The account's address, name, language and age; null when it no longer exists.</summary>
    Task<EmailRecipientCandidate?> GetAsync(Guid userId, CancellationToken ct = default);
}
