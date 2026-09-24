using WarpTalk.Shared.Email;

namespace WarpTalk.NotificationService.Application.DTOs.EmailTemplates;

public sealed record EmailTemplateVariableDto(string Name, string Description, string Sample, bool Required, bool Multiline);

/// <summary>The editable fields of one email in one locale (draft or published side).</summary>
public sealed record EmailContentFieldsDto(
    string Subject,
    string Preheader,
    string Heading,
    string BodyHtml,
    // Null derives the plain text from the HTML.
    string? TextBody,
    // Null uses the default layout.
    Guid? LayoutId);

public sealed record EmailVariantSummaryDto(
    Guid Id,
    string Locale,
    string Status,
    int PublishedVersion,
    bool HasDraftChanges,
    DateTime? PublishedAt,
    Guid? PublishedBy,
    DateTime DraftUpdatedAt,
    Guid DraftUpdatedBy);

public sealed record EmailDeliveryTotalsDto(int Sent, int Failed);

/// <summary>One card or row on the admin list.</summary>
public sealed record EmailTemplateListItemDto(
    string Key,
    string Name,
    string Description,
    string Service,
    string Provider,
    string Trigger,
    bool IsLive,
    string? DormantReason,
    IReadOnlyList<EmailTemplateVariableDto> Variables,
    // The subject senders use now in English, placeholders unfilled.
    string Subject,
    // The layout English uses now; null means the built-in layout.
    string? LayoutName,
    IReadOnlyList<EmailVariantSummaryDto> Variants,
    bool HasDraftChanges,
    DateTime? UpdatedAt,
    Guid? UpdatedBy,
    EmailDeliveryTotalsDto Last30Days);

public sealed record EmailVariantDto(
    Guid Id,
    string Locale,
    string Status,
    EmailContentFieldsDto Draft,
    EmailContentFieldsDto? Published,
    int PublishedVersion,
    bool HasDraftChanges,
    DateTime? PublishedAt,
    Guid? PublishedBy,
    DateTime DraftUpdatedAt,
    Guid DraftUpdatedBy,
    DateTime? ArchivedAt);

public sealed record EmailSampleDataSetDto(
    // Guid.Empty for the built-in set.
    Guid Id,
    string Name,
    IReadOnlyDictionary<string, string> Values,
    bool BuiltIn,
    DateTime? UpdatedAt);

public sealed record EmailTemplateDetailDto(
    EmailTemplateListItemDto Template,
    EmailContentFieldsDto Default,
    IReadOnlyList<EmailVariantDto> Variants,
    IReadOnlyList<EmailSampleDataSetDto> SampleSets,
    IReadOnlyList<string> SupportedLocales);

/// <summary>
/// PUT body for a draft. <see cref="ExpectedDraftUpdatedAt"/> refuses a save over someone else's
/// newer draft; null skips the check (the first save of a new locale).
/// </summary>
public sealed record SaveEmailDraftRequest(
    string Subject,
    string Preheader,
    string Heading,
    string BodyHtml,
    string? TextBody,
    Guid? LayoutId,
    DateTime? ExpectedDraftUpdatedAt = null);

public sealed record PublishEmailRequest(string? Note = null, int? ExpectedPublishedVersion = null);

public sealed record DuplicateEmailVariantRequest(string TargetLocale, bool Overwrite = false);

/// <summary>
/// An unsaved draft for the preview or the test email. Sample values come from, in order: the
/// catalog samples, the named set, then <see cref="Values"/>.
/// </summary>
public sealed record EmailPreviewRequest(
    string Locale,
    string Subject,
    string Preheader,
    string Heading,
    string BodyHtml,
    string? TextBody,
    Guid? LayoutId,
    Guid? SampleSetId = null,
    IReadOnlyDictionary<string, string>? Values = null,
    bool Dark = false);

public sealed record EmailPreviewDto(
    string Subject,
    string Preheader,
    string Html,
    string Text,
    IReadOnlyList<EmailTemplateIssue> Issues,
    string LayoutName);

public sealed record EmailTestSendRequest(
    string Locale,
    string Subject,
    string Preheader,
    string Heading,
    string BodyHtml,
    string? TextBody,
    Guid? LayoutId,
    IReadOnlyList<string> Recipients,
    Guid? SampleSetId = null,
    IReadOnlyDictionary<string, string>? Values = null);

public sealed record EmailTestSendDto(IReadOnlyList<string> SentTo, IReadOnlyList<string> Failed, string Subject);

/// <summary>A published version; <see cref="Fields"/> is the snapshot, compared field by field in the diff view.</summary>
public sealed record EmailCmsVersionDto(
    int Version,
    string Action,
    IReadOnlyDictionary<string, string?> Fields,
    string? Note,
    Guid CreatedBy,
    DateTime CreatedAt);

public sealed record SaveSampleDataSetRequest(string Name, IReadOnlyDictionary<string, string> Values);

public sealed record EmailStatsDayDto(DateOnly Day, int Sent, int Failed);

public sealed record EmailStatsLocaleDto(string Locale, int Sent, int Failed);

public sealed record EmailStatsDto(
    int Days,
    EmailDeliveryTotalsDto Totals,
    IReadOnlyList<EmailStatsDayDto> Daily,
    IReadOnlyList<EmailStatsLocaleDto> ByLocale);

/// <summary>
/// A bulk action over emails. publish / discard / archive act on <see cref="Locale"/> (or every
/// locale when null); duplicate copies <see cref="Locale"/> (default en) into
/// <see cref="TargetLocale"/>.
/// </summary>
public sealed record EmailBulkRequest(
    string Action,
    IReadOnlyList<string> Keys,
    string? Locale = null,
    string? TargetLocale = null);

// ── Blocks (layouts and partials) ──────────────────────────────────────────────────────────────

public sealed record EmailBlockFieldsDto(string Html, string? Text, string? DarkCss);

public sealed record EmailBlockDto(
    Guid Id,
    string Kind,
    string Key,
    string Name,
    string? Description,
    string Status,
    bool IsDefault,
    EmailBlockFieldsDto Draft,
    EmailBlockFieldsDto? Published,
    int PublishedVersion,
    bool HasDraftChanges,
    DateTime? PublishedAt,
    Guid? PublishedBy,
    DateTime DraftUpdatedAt,
    Guid DraftUpdatedBy,
    DateTime? ArchivedAt,
    DateTime CreatedAt,
    // "auth.verify-email (vi)", or for a block also "layout: Brand" — what publishing it affects.
    IReadOnlyList<string> UsedBy);

public sealed record CreateEmailBlockRequest(
    string Kind,
    string Key,
    string Name,
    string? Description,
    string Html,
    string? Text,
    string? DarkCss);

public sealed record SaveEmailBlockDraftRequest(
    string Name,
    string? Description,
    string Html,
    string? Text,
    string? DarkCss,
    DateTime? ExpectedDraftUpdatedAt = null);

public sealed record DuplicateEmailBlockRequest(string Key, string Name);

/// <summary>A draft block rendered around (layout) or inside (partial) one email's published content.</summary>
public sealed record EmailBlockPreviewRequest(
    string Html,
    string? Text,
    string? DarkCss,
    string? TemplateKey = null,
    string? Locale = null,
    bool Dark = false);

public sealed record EmailBlockBulkRequest(string Action, IReadOnlyList<Guid> Ids);
