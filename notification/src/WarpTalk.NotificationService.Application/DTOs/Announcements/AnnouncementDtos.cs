using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.Application.DTOs.Announcements;

/// <summary>An announcement as the admin sees it: everything, including who it is for.</summary>
public sealed record AdminAnnouncementDto(
    Guid Id,
    string Title,
    string BodyMarkdown,
    string Type,
    // DRAFT, PUBLISHED or ARCHIVED — what is stored.
    string Status,
    // DRAFT, SCHEDULED, PUBLISHED, ENDED or ARCHIVED — what it means right now.
    string EffectiveStatus,
    string Placement,
    string Variant,
    string AccentColor,
    string? Icon,
    string? ImageUrl,
    int Priority,
    bool Dismissible,
    string Frequency,
    string AudienceMode,
    IReadOnlyList<string> AudiencePlanSlugs,
    IReadOnlyList<Guid> AudienceWorkspaceIds,
    IReadOnlyList<string> TargetRoles,
    IReadOnlyList<string> TargetLocales,
    int? NewUsersWithinDays,
    string? CtaLabel,
    string? CtaUrl,
    string? SecondaryCtaLabel,
    string? SecondaryCtaUrl,
    DateTime? StartsAt,
    DateTime? EndsAt,
    DateTime? PublishedAt,
    DateTime? ArchivedAt,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>The custom email template sent to the same audience when it goes live, if any.</summary>
    public string? EmailTemplateKey { get; init; }

    /// <summary>The audience send that email channel created (see the template's Sends tab).</summary>
    public Guid? EmailCampaignId { get; init; }
}

public sealed record AdminAnnouncementPageDto(
    IReadOnlyList<AdminAnnouncementDto> Items,
    int Total,
    int Page,
    int PageSize,
    // Per effective status, under the same search and filters. Drives the filter chips' counts.
    IReadOnlyDictionary<string, int> Counts);

public sealed record AdminAnnouncementListQuery(
    int Page = 1,
    int PageSize = 24,
    string? Status = null,
    string? Search = null,
    string? Type = null,
    string? Placement = null,
    // updated, created, priority, title or starts.
    string? Sort = null,
    // asc or desc.
    string? Order = null);

/// <summary>
/// Create and edit body. Publishing is its own action. Everything after <see cref="EndsAt"/> was
/// added with CMS v2 and defaults to what a v1 announcement meant: a subtle top banner, shown to
/// its audience until dismissed.
/// </summary>
public sealed record UpsertAnnouncementRequest(
    string Title,
    string BodyMarkdown,
    string Type,
    string AudienceMode,
    IReadOnlyList<string>? AudiencePlanSlugs,
    IReadOnlyList<Guid>? AudienceWorkspaceIds,
    string? CtaLabel,
    string? CtaUrl,
    DateTime? StartsAt,
    DateTime? EndsAt,
    string Placement = AnnouncementConstants.PlacementTopBanner,
    string Variant = "SUBTLE",
    string AccentColor = "BRAND",
    string? Icon = null,
    string? ImageUrl = null,
    int Priority = 0,
    bool Dismissible = true,
    string Frequency = AnnouncementConstants.FrequencyUntilDismissed,
    IReadOnlyList<string>? TargetRoles = null,
    IReadOnlyList<string>? TargetLocales = null,
    int? NewUsersWithinDays = null,
    string? SecondaryCtaLabel = null,
    string? SecondaryCtaUrl = null,
    // Optional email channel: a custom email template sent to the same audience when it goes live.
    string? EmailTemplateKey = null);

/// <summary>Publish body. <see cref="StartsAt"/> null means "now"; a future instant schedules it.</summary>
public sealed record PublishAnnouncementRequest(DateTime? StartsAt = null);

/// <summary>publish, archive, delete (drafts only) or duplicate.</summary>
public sealed record AnnouncementBulkRequest(string Action, IReadOnlyList<Guid> Ids);

/// <summary>An announcement as the people it is for see it. No audience: that is admin data.</summary>
public sealed record ViewerAnnouncementDto(
    Guid Id,
    string Title,
    string BodyMarkdown,
    string Type,
    string Placement,
    string Variant,
    string AccentColor,
    string? Icon,
    string? ImageUrl,
    int Priority,
    bool Dismissible,
    string Frequency,
    string? CtaLabel,
    string? CtaUrl,
    string? SecondaryCtaLabel,
    string? SecondaryCtaUrl,
    DateTime? StartsAt,
    DateTime? EndsAt,
    DateTime? PublishedAt);

/// <summary>
/// What a viewer did: IMPRESSION, DISMISS, CTA_CLICK or SECONDARY_CLICK. <see cref="SessionId"/> is
/// a random id the web app keeps per browser session; it is what "once", "every session" and
/// "daily" are measured against.
/// </summary>
public sealed record AnnouncementEventRequest(string Type, string? SessionId = null);

public sealed record AnnouncementAnalyticsTotalsDto(
    int UniqueViewers,
    int Impressions,
    int Dismissals,
    int UniqueCtaClickers,
    int CtaClicks,
    int SecondaryClicks,
    // Unique clickers ÷ unique viewers, 0–1.
    double ClickThroughRate,
    // Dismissals ÷ unique viewers, 0–1.
    double DismissRate);

public sealed record AnnouncementAnalyticsDayDto(DateOnly Day, int Impressions, int Dismissals, int CtaClicks, int SecondaryClicks);

public sealed record AnnouncementAnalyticsDto(int Days, AnnouncementAnalyticsTotalsDto Totals, IReadOnlyList<AnnouncementAnalyticsDayDto> Daily);

public sealed record AnnouncementAssetDto(Guid Id, string Url, string FileName, string ContentType, int SizeBytes);

/// <summary>An uploaded image as the asset endpoint serves it.</summary>
public sealed record AnnouncementAssetContent(string ContentType, byte[] Content);
