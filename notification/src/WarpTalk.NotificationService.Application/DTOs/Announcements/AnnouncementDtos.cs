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
    string AudienceMode,
    IReadOnlyList<string> AudiencePlanSlugs,
    IReadOnlyList<Guid> AudienceWorkspaceIds,
    string? CtaLabel,
    string? CtaUrl,
    DateTime? StartsAt,
    DateTime? EndsAt,
    DateTime? PublishedAt,
    DateTime? ArchivedAt,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AdminAnnouncementPageDto(
    IReadOnlyList<AdminAnnouncementDto> Items,
    int Total,
    int Page,
    int PageSize,
    // Per effective status, under the same search. Drives the filter tabs' counts.
    IReadOnlyDictionary<string, int> Counts);

public sealed record AdminAnnouncementListQuery(
    int Page = 1,
    int PageSize = 24,
    string? Status = null,
    string? Search = null);

/// <summary>Create and edit body. Publishing is its own action.</summary>
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
    DateTime? EndsAt);

/// <summary>
/// Publish body. <see cref="StartsAt"/> null means "now"; a future instant schedules it.
/// </summary>
public sealed record PublishAnnouncementRequest(DateTime? StartsAt = null);

/// <summary>An announcement as the people it is for see it. No audience: that is admin data.</summary>
public sealed record ViewerAnnouncementDto(
    Guid Id,
    string Title,
    string BodyMarkdown,
    string Type,
    string? CtaLabel,
    string? CtaUrl,
    DateTime? StartsAt,
    DateTime? EndsAt,
    DateTime? PublishedAt);
