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
        var secondaryUrl = string.IsNullOrWhiteSpace(request.SecondaryCtaUrl) ? null : request.SecondaryCtaUrl.Trim();
        var secondaryLabel = string.IsNullOrWhiteSpace(request.SecondaryCtaLabel) ? null : request.SecondaryCtaLabel.Trim();
        var roles = (request.TargetRoles ?? [])
            .Select(role => AnnouncementConstants.Roles.FirstOrDefault(known => string.Equals(known, role?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? (role ?? string.Empty).Trim())
            .Where(role => role.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var locales = (request.TargetLocales ?? [])
            .Select(locale => (locale ?? string.Empty).Trim().ToLowerInvariant())
            .Where(locale => locale.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

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
            EmailTemplateKey = string.IsNullOrWhiteSpace(request.EmailTemplateKey) ? null : request.EmailTemplateKey.Trim().ToLowerInvariant(),
            Placement = (request.Placement ?? string.Empty).Trim().ToUpperInvariant(),
            Variant = (request.Variant ?? string.Empty).Trim().ToUpperInvariant(),
            AccentColor = (request.AccentColor ?? string.Empty).Trim().ToUpperInvariant(),
            Icon = string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim().ToLowerInvariant(),
            ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim(),
            Frequency = (request.Frequency ?? string.Empty).Trim().ToUpperInvariant(),
            TargetRoles = roles,
            TargetLocales = locales,
            SecondaryCtaLabel = secondaryLabel,
            SecondaryCtaUrl = secondaryUrl,
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

        if (request.SecondaryCtaUrl is null && request.SecondaryCtaLabel is not null)
            return "The second button's label needs a link.";
        if (request.SecondaryCtaUrl is not null)
        {
            if (request.CtaUrl is null) return "Add the main button before a second one.";
            if (request.SecondaryCtaLabel is null) return "The second button's link needs a label.";
            if (request.SecondaryCtaLabel.Length > AnnouncementConstants.MaxCtaLabelLength)
                return $"The second button's label must be {AnnouncementConstants.MaxCtaLabelLength} characters or fewer.";
            if (request.SecondaryCtaUrl.Length > AnnouncementConstants.MaxCtaUrlLength || !IsAllowedLink(request.SecondaryCtaUrl))
                return "The second button's link must be an https:// or http:// address, or a path in the app starting with /.";
        }

        if (request.StartsAt is { } start && request.EndsAt is { } end && end <= start)
            return "The end of the window must be after its start.";

        if (!AnnouncementConstants.Placements.Contains(request.Placement))
            return $"Placement must be one of {string.Join(", ", AnnouncementConstants.Placements)}.";
        if (!AnnouncementConstants.Variants.Contains(request.Variant))
            return $"Style must be one of {string.Join(", ", AnnouncementConstants.Variants)}.";
        if (!AnnouncementConstants.AccentColors.Contains(request.AccentColor))
            return $"Color must be one of {string.Join(", ", AnnouncementConstants.AccentColors)}.";
        if (request.Icon is not null && !AnnouncementConstants.Icons.Contains(request.Icon))
            return $"'{request.Icon}' is not an icon the app can draw.";
        if (request.ImageUrl is not null
            && (request.ImageUrl.Length > AnnouncementConstants.MaxImageUrlLength || !IsAllowedImage(request.ImageUrl)))
            return "The image must be an uploaded image or an https:// address.";
        if (request.Priority is < 0 or > AnnouncementConstants.MaxPriority)
            return $"Priority must be between 0 and {AnnouncementConstants.MaxPriority}.";
        if (!AnnouncementConstants.Frequencies.Contains(request.Frequency))
            return $"Frequency must be one of {string.Join(", ", AnnouncementConstants.Frequencies)}.";

        var roles = request.TargetRoles ?? [];
        var badRole = roles.FirstOrDefault(role => !AnnouncementConstants.Roles.Contains(role));
        if (badRole is not null) return $"'{badRole}' is not a workspace role. Use {string.Join(", ", AnnouncementConstants.Roles)}.";
        var locales = request.TargetLocales ?? [];
        var badLocale = locales.FirstOrDefault(locale => !AnnouncementConstants.Locales.Contains(locale));
        if (badLocale is not null) return $"'{badLocale}' is not a language the app speaks. Use {string.Join(", ", AnnouncementConstants.Locales)}.";
        if (request.NewUsersWithinDays is { } days && (days < 1 || days > AnnouncementConstants.MaxNewUserDays))
            return $"\"New users\" must be between 1 and {AnnouncementConstants.MaxNewUserDays} days.";

        return null;
    }

    /// <summary>An uploaded announcement asset, or an https image anywhere.</summary>
    public static bool IsAllowedImage(string url)
    {
        if (url.StartsWith(AssetPathPrefix, StringComparison.Ordinal))
            return Guid.TryParse(url[AssetPathPrefix.Length..], out _);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !url.Any(char.IsWhiteSpace);
    }

    /// <summary>Where an uploaded asset is served (under the gateway's /api/v1/notifications route).</summary>
    public const string AssetPathPrefix = "/api/v1/notifications/announcements/assets/";

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
