using System.Text.RegularExpressions;
using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.Application.Helpers.Announcements;

/// <summary>
/// What a valid announcement is. Pure, so the web client's mirror of these rules
/// (src/lib/announcements/announcement-cms.ts) has something exact to be checked against.
/// </summary>
public static partial class AnnouncementRules
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,79}$")]
    private static partial Regex PlanSlugPattern();

    /// <summary>The request with every field trimmed and the audience lists the mode ignores emptied.</summary>
    public static UpsertAnnouncementRequest Normalize(UpsertAnnouncementRequest request)
    {
        var mode = (request.AudienceMode ?? string.Empty).Trim().ToUpperInvariant();
        var plans = mode == AnnouncementConstants.AudiencePlans
            ? (request.AudiencePlanSlugs ?? []).Select(slug => (slug ?? string.Empty).Trim().ToLowerInvariant())
                .Where(slug => slug.Length > 0).Distinct(StringComparer.Ordinal).ToList()
            : [];
        var workspaces = mode == AnnouncementConstants.AudienceWorkspaces
            ? (request.AudienceWorkspaceIds ?? []).Distinct().ToList()
            : [];

        var ctaUrl = string.IsNullOrWhiteSpace(request.CtaUrl) ? null : request.CtaUrl.Trim();
        var ctaLabel = string.IsNullOrWhiteSpace(request.CtaLabel) ? null : request.CtaLabel.Trim();

        return request with
        {
            Title = (request.Title ?? string.Empty).Trim(),
            BodyMarkdown = (request.BodyMarkdown ?? string.Empty).Trim(),
            Type = (request.Type ?? string.Empty).Trim().ToUpperInvariant(),
            AudienceMode = mode,
            AudiencePlanSlugs = plans,
            AudienceWorkspaceIds = workspaces,
            CtaLabel = ctaLabel,
            CtaUrl = ctaUrl,
            StartsAt = AsUtc(request.StartsAt),
            EndsAt = AsUtc(request.EndsAt),
        };
    }

    /// <summary>The first thing wrong with a normalized request, or null.</summary>
    public static string? Validate(UpsertAnnouncementRequest request)
    {
        if (request.Title.Length == 0) return "A title is required.";
        if (request.Title.Length > AnnouncementConstants.MaxTitleLength)
            return $"The title must be {AnnouncementConstants.MaxTitleLength} characters or fewer.";

        if (request.BodyMarkdown.Length == 0) return "The body is required.";
        if (request.BodyMarkdown.Length > AnnouncementConstants.MaxBodyLength)
            return $"The body must be {AnnouncementConstants.MaxBodyLength:N0} characters or fewer.";

        if (!AnnouncementConstants.Types.Contains(request.Type))
            return $"Type must be one of {string.Join(", ", AnnouncementConstants.Types)}.";

        if (!AnnouncementConstants.AudienceModes.Contains(request.AudienceMode))
            return $"Audience must be one of {string.Join(", ", AnnouncementConstants.AudienceModes)}.";

        var plans = request.AudiencePlanSlugs ?? [];
        var workspaces = request.AudienceWorkspaceIds ?? [];
        if (request.AudienceMode == AnnouncementConstants.AudiencePlans)
        {
            if (plans.Count == 0) return "Choose at least one plan.";
            if (plans.Count > AnnouncementConstants.MaxAudienceEntries)
                return $"Choose {AnnouncementConstants.MaxAudienceEntries} plans or fewer.";
            var bad = plans.FirstOrDefault(slug => !PlanSlugPattern().IsMatch(slug));
            if (bad is not null) return $"'{bad}' is not a plan slug.";
        }
        if (request.AudienceMode == AnnouncementConstants.AudienceWorkspaces)
        {
            if (workspaces.Count == 0) return "Choose at least one workspace.";
            if (workspaces.Count > AnnouncementConstants.MaxAudienceEntries)
                return $"Choose {AnnouncementConstants.MaxAudienceEntries} workspaces or fewer.";
            if (workspaces.Any(id => id == Guid.Empty)) return "A workspace id is empty.";
        }

        if (request.CtaUrl is null && request.CtaLabel is not null)
            return "A button label needs a link.";
        if (request.CtaUrl is not null)
        {
            if (request.CtaLabel is null) return "A link needs a button label.";
            if (request.CtaLabel.Length > AnnouncementConstants.MaxCtaLabelLength)
                return $"The button label must be {AnnouncementConstants.MaxCtaLabelLength} characters or fewer.";
            if (request.CtaUrl.Length > AnnouncementConstants.MaxCtaUrlLength)
                return "The link is too long.";
            if (!IsAllowedLink(request.CtaUrl))
                return "The link must be an https:// or http:// address, or a path in the app starting with /.";
        }

        if (request.StartsAt is { } start && request.EndsAt is { } end && end <= start)
            return "The end of the window must be after its start.";

        return null;
    }

    /// <summary>An absolute http(s) URL, or an in-app path ("/…" but not "//…", which is another host).</summary>
    public static bool IsAllowedLink(string link)
    {
        if (link.Any(char.IsWhiteSpace)) return false;
        if (link.StartsWith('/')) return !link.StartsWith("//", StringComparison.Ordinal) && !link.StartsWith("/\\", StringComparison.Ordinal);
        return Uri.TryCreate(link, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
               && !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>
    /// Npgsql refuses to write a non-UTC DateTime into timestamptz. A value without an offset is
    /// taken to be UTC, which is what the web client sends (toISOString).
    /// </summary>
    public static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } utc => utc,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        { } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
    };
}
