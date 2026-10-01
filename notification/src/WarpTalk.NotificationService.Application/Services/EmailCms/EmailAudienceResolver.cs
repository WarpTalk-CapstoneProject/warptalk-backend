using WarpTalk.NotificationService.Application.DTOs.EmailTemplates;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.Services.EmailCms;

/// <summary>An audience, normalized: the announcement targeting rules, applied to email.</summary>
public sealed record EmailAudienceSpec(
    string Mode,
    IReadOnlyList<string> PlanSlugs,
    IReadOnlyList<Guid> WorkspaceIds,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Locales,
    int? NewUsersWithinDays)
{
    public static (EmailAudienceSpec? Spec, string? Error) From(EmailAudienceDto? dto)
    {
        if (dto is null) return (null, "Choose who the email is for.");
        var mode = (dto.Mode ?? string.Empty).Trim().ToUpperInvariant();
        if (!AnnouncementConstants.AudienceModes.Contains(mode))
            return (null, $"Audience must be one of {string.Join(", ", AnnouncementConstants.AudienceModes)}.");
        var plans = mode == AnnouncementConstants.AudiencePlans
            ? (dto.PlanSlugs ?? []).Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Distinct().ToList()
            : [];
        var workspaces = mode == AnnouncementConstants.AudienceWorkspaces ? (dto.WorkspaceIds ?? []).Distinct().ToList() : [];
        if (mode == AnnouncementConstants.AudiencePlans && plans.Count == 0) return (null, "Choose at least one plan.");
        if (mode == AnnouncementConstants.AudienceWorkspaces && workspaces.Count == 0) return (null, "Choose at least one workspace.");
        if (plans.Count > AnnouncementConstants.MaxAudienceEntries || workspaces.Count > AnnouncementConstants.MaxAudienceEntries)
            return (null, $"Choose {AnnouncementConstants.MaxAudienceEntries} plans or workspaces at most.");

        var roles = new List<string>();
        foreach (var role in dto.Roles ?? [])
        {
            var match = AnnouncementConstants.Roles.FirstOrDefault(r => string.Equals(r, role?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null) return (null, $"Role must be one of {string.Join(", ", AnnouncementConstants.Roles)}.");
            if (!roles.Contains(match)) roles.Add(match);
        }
        var locales = new List<string>();
        foreach (var locale in dto.Locales ?? [])
        {
            var normalized = EmailLocales.Normalize(locale);
            if (normalized is null) return (null, $"Language must be one of {string.Join(", ", EmailLocales.Supported)}.");
            if (!locales.Contains(normalized)) locales.Add(normalized);
        }
        if (dto.NewUsersWithinDays is { } days && (days < 1 || days > 365))
            return (null, "“Joined in the last … days” must be between 1 and 365.");

        return (new EmailAudienceSpec(mode, plans, workspaces, roles, locales, dto.NewUsersWithinDays), null);
    }

    public EmailAudienceDto ToDto() => new(Mode, PlanSlugs, WorkspaceIds, Roles, Locales, NewUsersWithinDays);
}

/// <summary>One person a send resolved to.</summary>
public sealed record EmailAudienceMember(Guid UserId, string Email, string? FullName, string Locale);

public sealed record EmailAudienceResult(IReadOnlyList<EmailAudienceMember> Members, int Candidates);

public interface IEmailAudienceResolver
{
    Task<Result<EmailAudienceResult>> ResolveAsync(EmailAudienceSpec spec, int maxRecipients, CancellationToken ct = default);
}

/// <summary>
/// Turns an audience into people: the base set (everyone, members of workspaces on some plans,
/// members of some workspaces) from the directory, then the same narrowing announcements apply —
/// role in any workspace, app language, account age. Each person is resolved once.
/// </summary>
public sealed class EmailAudienceResolver : IEmailAudienceResolver
{
    private const int Parallelism = 8;

    private readonly IEmailRecipientDirectory _directory;
    private readonly IViewerAudienceResolver _viewer;
    private readonly TimeProvider _time;

    public EmailAudienceResolver(IEmailRecipientDirectory directory, IViewerAudienceResolver viewer, TimeProvider? timeProvider = null)
    {
        _directory = directory;
        _viewer = viewer;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<EmailAudienceResult>> ResolveAsync(EmailAudienceSpec spec, int maxRecipients, CancellationToken ct = default)
    {
        // One more than the cap, so "over the cap" is detectable without listing everyone.
        var limit = maxRecipients + 1;
        var candidates = spec.Mode switch
        {
            AnnouncementConstants.AudiencePlans => await _directory.ListMembersOfPlansAsync(spec.PlanSlugs, limit, ct),
            AnnouncementConstants.AudienceWorkspaces => await _directory.ListMembersOfWorkspacesAsync(spec.WorkspaceIds, limit, ct),
            _ => await _directory.ListActiveUserIdsAsync(limit, ct),
        };
        if (!candidates.IsSuccess) return Result.Failure<EmailAudienceResult>(candidates.Error!, candidates.ErrorCode ?? ErrorCodes.ServiceUnavailable);
        var ids = candidates.Value!.Distinct().ToList();
        if (ids.Count > maxRecipients)
        {
            return Result.Failure<EmailAudienceResult>(
                $"This audience has more than {maxRecipients:N0} people. Narrow it (plans, workspaces, roles, language) and send in parts.",
                ErrorCodes.ValidationError);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var members = new System.Collections.Concurrent.ConcurrentBag<EmailAudienceMember>();
        string? failure = null;
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (id, token) =>
        {
            var person = await _directory.GetAsync(id, token);
            if (person is null || string.IsNullOrWhiteSpace(person.Email)) return;
            var locale = EmailLocales.Normalize(person.PreferredLanguage) ?? EmailLocales.Default;

            if (spec.Locales.Count > 0 && !spec.Locales.Contains(locale)) return;
            if (spec.NewUsersWithinDays is { } days && (person.CreatedAt is not { } created || created < now.AddDays(-days))) return;
            if (spec.Roles.Count > 0)
            {
                var audience = await _viewer.ResolveAsync(id, includeAccountAge: false, token);
                if (!audience.IsSuccess)
                {
                    failure ??= "Could not read workspace roles. Try again in a moment.";
                    return;
                }
                if (!spec.Roles.Any(role => audience.Value!.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))) return;
            }
            members.Add(new EmailAudienceMember(id, person.Email.Trim(), person.FullName, locale));
        });

        // Fail closed: sending to a role-scoped audience with some roles unknown would leak it.
        if (failure is not null) return Result.Failure<EmailAudienceResult>(failure, ErrorCodes.ServiceUnavailable);

        var unique = members
            .GroupBy(m => m.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Result.Success(new EmailAudienceResult(unique, ids.Count));
    }
}
