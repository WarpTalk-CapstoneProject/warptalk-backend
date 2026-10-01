namespace WarpTalk.NotificationService.Application.DTOs.EmailTemplates;

/// <summary>A variable on a custom template, as the wizard declares it.</summary>
public sealed record CustomEmailVariableRequest(string Name, string? Label, string? Type, string? Sample, bool Required = false);

/// <summary>One locale's starting content in the create wizard.</summary>
public sealed record CustomEmailLocaleContent(
    string Locale,
    string Subject,
    string? Preheader,
    string? Heading,
    string BodyHtml,
    string? TextBody);

/// <summary>
/// POST body for a new custom template. The content is saved as drafts; nothing is sent until the
/// template is published and then sent to an audience or with an announcement.
/// </summary>
public sealed record CreateCustomEmailTemplateRequest(
    string Key,
    string Name,
    string? Description,
    string Category,
    IReadOnlyList<CustomEmailVariableRequest>? Variables,
    Guid? LayoutId,
    IReadOnlyList<CustomEmailLocaleContent>? Content);

/// <summary>PUT body for a custom template's details (content is edited per locale, like any email).</summary>
public sealed record UpdateCustomEmailTemplateRequest(
    string Name,
    string? Description,
    string Category,
    IReadOnlyList<CustomEmailVariableRequest>? Variables);

public sealed record DeleteCustomEmailTemplateRequest(string? Reason, bool Permanent = false);

/// <summary>What deleting would do, so the dialog can offer "delete for good" only when it is allowed.</summary>
public sealed record CustomEmailDeletionCheckDto(bool CanDeletePermanently, string? Reason, int CampaignCount, int SentCount);

// ── Audience sends ─────────────────────────────────────────────────────────────────────────

/// <summary>Who an audience send goes to: the announcement targeting rules, reused.</summary>
public sealed record EmailAudienceDto(
    string Mode,
    IReadOnlyList<string>? PlanSlugs,
    IReadOnlyList<Guid>? WorkspaceIds,
    IReadOnlyList<string>? Roles,
    IReadOnlyList<string>? Locales,
    int? NewUsersWithinDays);

public sealed record EmailSendEstimateRequest(EmailAudienceDto Audience);

public sealed record EmailSendEstimateDto(
    int Recipients,
    int SkippedOptedOut,
    IReadOnlyList<EmailStatsLocaleCountDto> ByLocale,
    // Locales some recipients speak that have nothing published: they are sent English.
    IReadOnlyList<string> FallbackLocales,
    int MaxRecipients,
    int SendsPerMinute);

public sealed record EmailStatsLocaleCountDto(string Locale, int Count);

/// <summary>
/// POST body that starts a send. <see cref="ExpectedRecipients"/> is the count the admin confirmed;
/// if the audience grew or shrank by more than a few since the estimate, the send is refused so the
/// admin confirms the real number.
/// </summary>
public sealed record CreateEmailSendRequest(
    EmailAudienceDto Audience,
    IReadOnlyDictionary<string, string>? Values,
    int ExpectedRecipients,
    DateTime? ScheduledAt = null);

public sealed record EmailCampaignDto(
    Guid Id,
    string TemplateKey,
    string TemplateName,
    string Source,
    Guid? AnnouncementId,
    EmailAudienceDto Audience,
    IReadOnlyDictionary<string, string> Values,
    string Status,
    DateTime ScheduledAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int Total,
    int Sent,
    int Failed,
    int Skipped,
    string? Error,
    Guid CreatedBy,
    DateTime CreatedAt,
    Guid? CancelledBy,
    DateTime? CancelledAt);

public sealed record EmailCampaignRecipientDto(
    Guid UserId,
    string Email,
    string? FullName,
    string Locale,
    string Status,
    string? Error,
    DateTime? SentAt);

public sealed record EmailCampaignRecipientPageDto(
    IReadOnlyList<EmailCampaignRecipientDto> Items,
    int Total,
    int Page,
    int PageSize);
